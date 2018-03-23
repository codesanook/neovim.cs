using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Neovim;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using QuickFont;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using log4net;

namespace NeovimDemo
{
    public partial class MainWindow : Form
    {
        private readonly SynchronizationContext _uiContext;
        private NeovimClient _neovim;

        private RectangleF _cursor;
        private RectangleF _scrollRegion;
        private int _rows = 24;
        private int _columns = 80;
        private float _width;
        private float _height;
        private FontGroup _font;
        private Color _fgColor = Color.White;
        private Color _bgColor = Color.DarkSlateGray;

        private FrameBuffer _backBuffer;
        private FrameBuffer _pingPongBuffer;
        private char[] charBuffer;
//https://msdn.microsoft.com/en-us/library/windows/desktop/dd375731(v=vs.85).aspx
        private IDictionary<string, int> commandCode = new Dictionary<string, int>
        {
            { "c", 0x11}
        };

        private static readonly ILog log = LogManager.GetLogger(nameof(MainWindow));

        public MainWindow()
        {
            log.Debug("main windows");
            InitializeComponent();

            this.SuspendLayout();
            // 
            // glControl
            // 
            this.glControl = new OpenTK.GLControl();
            this.glControl.BackColor = System.Drawing.Color.Black;
            this.glControl.Font = new System.Drawing.Font("Consolas", 15F,
                System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.glControl.Location = new System.Drawing.Point(0, 0);
            this.glControl.Margin = new System.Windows.Forms.Padding(7, 7, 7, 7);
            this.glControl.Name = "glControl";
            this.glControl.Size = new System.Drawing.Size(this.mainPanel.Size.Width - 50, 480);
            this.glControl.TabIndex = 0;
            this.glControl.VSync = false;
            this.glControl.Load += new System.EventHandler(this.glControl_Load);
            this.glControl.Paint += new System.Windows.Forms.PaintEventHandler(this.glControl_Paint);
            this.glControl.KeyDown += new System.Windows.Forms.KeyEventHandler(this.glControl_KeyDown);


            this.mainPanel.Controls.Add(this.glControl);

            // 
            // MainWindow
            // 
            //this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            //this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            //this.AutoSize = true;
            //this.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            //this.ClientSize = new System.Drawing.Size(882, 483);
            //this.Margin = new System.Windows.Forms.Padding(4, 40, 4, 4);
            //this.ClientSize = new System.Drawing.Size(284, 261);
            this.Name = "MainWindow";
            this.Text = "Neovim";

            this.ResumeLayout(false);




            _uiContext = SynchronizationContext.Current;
            _neovim = new NeovimClient(@"C:\Program Files\Neovim\bin\nvim.exe");
            // Event is asynchronous so we need to handle the redraw event in the UI thread
            _neovim.Redraw += (o, args) => _uiContext.Post(x => NeovimOnRedraw(o, args), null);
            _neovim.ui_attach(_columns, _rows, true);
        }

        private Color ColorFromRgb(long rgb)
        {
            byte r = (byte)(rgb >> 16);
            byte g = (byte)(rgb >> 8);
            byte b = (byte)(rgb >> 0);
            return Color.FromArgb(r, g, b);
        }

        private void NeovimOnRedraw(object sender, NeovimRedrawEventArgs e)
        {
            bool shouldInvalidate = false;

            _backBuffer.Bind();
            foreach (var method in e.Methods)
            {
                switch (method.Method)
                {
                    case RedrawMethodType.Clear:
                        shouldInvalidate = true;
                        GL.Clear(ClearBufferMask.ColorBufferBit);
                        break;

                    case RedrawMethodType.Resize:
                        var columns = method.Params[0][1].AsInteger();
                        var rows = method.Params[0][0].AsInteger();
                        if (rows == _rows && columns == _columns) return;

                        break;

                    case RedrawMethodType.UpdateForeground:
                        _font.Color = ColorFromRgb(method.Params[0][0].AsInteger());
                        break;

                    case RedrawMethodType.UpdateBackground:
                        _bgColor = ColorFromRgb(method.Params[0][0].AsInteger());
                        GL.ClearColor(_bgColor);
                        break;

                    case RedrawMethodType.HighlightSet:
                        foreach (var arg in method.Params)
                        {
                            var dict = arg[0].AsDictionary();

                            foreach (var entry in dict)
                            {
                                var str = entry.Key.AsString(Encoding.Default);
                                if (str == "foreground")
                                    _font.Color = ColorFromRgb(entry.Value.AsInteger());
                                else if (str == "background") { }
                                else if (str == "bold")
                                    if (entry.Value.AsBoolean())
                                        _font.FontStyle |= FontStyle.Bold;
                                    else _font.FontStyle &= ~FontStyle.Bold;
                                else if (str == "italic")
                                    if (entry.Value.AsBoolean())
                                        _font.FontStyle |= FontStyle.Italic;
                                    else _font.FontStyle &= FontStyle.Italic;
                            }
                        }
                        break;

                    case RedrawMethodType.EolClear:
                        shouldInvalidate = true;
                        DrawRectangle(new RectangleF(_cursor.X, _cursor.Y, _columns * _width - _cursor.X, _height), _bgColor);
                        break;

                    case RedrawMethodType.SetTitle:
                        Text = method.Params[0][0].AsString(Encoding.Default);
                        break;

                    case RedrawMethodType.Put:
                        shouldInvalidate = true;
                        List<byte> bytes = new List<byte>();
                        foreach (var arg in method.Params)
                            bytes.AddRange(arg[0].AsBinary());

                        var text = Encoding.Default.GetString(bytes.ToArray());
                        var tSize = _font.Measure(text);

                        DrawRectangle(new RectangleF(_cursor.Location, tSize), _bgColor);

                        GL.Enable(EnableCap.Blend);
                        _font.Print(text, new Vector2(_cursor.X, _cursor.Y));
                        GL.Disable(EnableCap.Blend);
                        GL.Color4(Color.White);

                        _cursor.X += tSize.Width;
                        if (_cursor.X >= _columns * _width) // Dont know if this is needed
                        {
                            _cursor.X = 0;
                            _cursor.Y += _height;
                        }
                        break;

                    case RedrawMethodType.CursorGoto:
                        shouldInvalidate = true;
                        _cursor.Y = method.Params[0][0].AsInteger() * _height;
                        _cursor.X = method.Params[0][1].AsInteger() * _width;
                        break;

                    case RedrawMethodType.Scroll:
                        // Amount to scroll
                        var count = method.Params[0][0].AsSByte();
                        if (count == 0) return;

                        var srcRect = new RectangleF();
                        var dstRect = new RectangleF();
                        var clearRect = new RectangleF();

                        // Scroll up
                        if (count >= 1)
                        {
                            srcRect = new RectangleF(_scrollRegion.X, _scrollRegion.Y + _height, _scrollRegion.Width,
                                _scrollRegion.Height - _height);
                            dstRect = new RectangleF(_scrollRegion.X, _scrollRegion.Y, _scrollRegion.Width,
                                _scrollRegion.Height - _height);
                            clearRect = new RectangleF(_scrollRegion.X, _scrollRegion.Y + _scrollRegion.Height - _height,
                                _scrollRegion.Width, _height + 1);
                        }
                        // Scroll down
                        else if (count <= -1)
                        {
                            srcRect = new RectangleF(_scrollRegion.X, _scrollRegion.Y, _scrollRegion.Width,
                                _scrollRegion.Height - _height);
                            dstRect = new RectangleF(_scrollRegion.X, _scrollRegion.Y + _height, _scrollRegion.Width,
                                _scrollRegion.Height - _height);
                            clearRect = new RectangleF(_scrollRegion.X, _scrollRegion.Y, _scrollRegion.Width,
                                _height + 1);
                        }

                        _pingPongBuffer.Bind();
                        _backBuffer.Texture.Bind();

                        DrawTexturedRectangle(srcRect, dstRect);

                        _backBuffer.Bind();
                        _pingPongBuffer.Texture.Bind();

                        DrawTexturedRectangle(dstRect, dstRect);

                        Texture2D.Unbind();

                        DrawRectangle(clearRect, _bgColor);
                        break;

                    case RedrawMethodType.SetScrollRegion:
                        var x = method.Params[0][2].AsInteger() * _width;
                        var y = method.Params[0][0].AsInteger() * _height;
                        var width = (method.Params[0][3].AsInteger() + 1) * _width;
                        var height = (method.Params[0][1].AsInteger() + 1) * _height;

                        _scrollRegion = new RectangleF(x, y, width, height);
                        break;

                    case RedrawMethodType.ModeChange:
                        shouldInvalidate = true;
                        var mode = method.Params[0][0].AsString(Encoding.Default);
                        if (mode == "insert")
                            _cursor.Width = _width / 4;
                        else if (mode == "normal")
                            _cursor.Width = _width;
                        break;

                        //        case RedrawMethodType.BusyStart:
                        //            break;

                        //        case RedrawMethodType.BusyStop:
                        //            break;

                        //        case RedrawMethodType.MouseOn:
                        //            break;

                        //        case RedrawMethodType.MouseOff:
                        //            break;
                }
            }
            FrameBuffer.Unbind();
            if (shouldInvalidate)
                glControl.Invalidate();
        }


        private void glControl_Load(object sender, EventArgs e)
        {
            _font = new FontGroup(glControl.Font);
            _font.Color = _fgColor;

            _width = _font.MonoSpaceWidth;
            _height = _font.LineSpacing;
            glControl.Width = Convert.ToInt32(_width * _columns);
            glControl.Height = Convert.ToInt32(_height * _rows);

            _cursor = new RectangleF(0, 0, _width, _height);

            _backBuffer = new FrameBuffer(glControl.Width, glControl.Height);
            _pingPongBuffer = new FrameBuffer(glControl.Width, glControl.Height);

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, glControl.Height, 0, -1, 1);
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.ClearColor(_bgColor);
        }

        private void glControl_Paint(object sender, PaintEventArgs e)
        {
            _backBuffer.Texture.Bind();

            GL.Begin(PrimitiveType.Quads);

            // Backbuffer needs inverted TexCoords, origin of TexCoords is bottom-left corner
            GL.TexCoord2(0, 1); GL.Vertex2(0, 0);
            GL.TexCoord2(1, 1); GL.Vertex2(glControl.Width, 0);
            GL.TexCoord2(1, 0); GL.Vertex2(glControl.Width, glControl.Height);
            GL.TexCoord2(0, 0); GL.Vertex2(0, glControl.Height);

            GL.End();

            Texture2D.Unbind();

            // Invert cursor color depending on the background for now
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactorSrc.OneMinusDstColor, BlendingFactorDest.Zero);
            DrawRectangle(_cursor, Color.White);
            GL.Disable(EnableCap.Blend);

            glControl.SwapBuffers();
        }

        private void DrawRectangle(RectangleF rect, Color color)
        {
            GL.Color3(color);

            GL.Begin(PrimitiveType.Quads);

            GL.Vertex2(rect.X, rect.Y);
            GL.Vertex2(rect.X + rect.Width, rect.Y);
            GL.Vertex2(rect.X + rect.Width, rect.Y + rect.Height);
            GL.Vertex2(rect.X, rect.Y + rect.Height);

            GL.End();

            GL.Color4(Color.White);
        }

        private void DrawTexturedRectangle(RectangleF rectSrc, RectangleF rectDst)
        {
            GL.Begin(PrimitiveType.Quads);

            var wScale = 1.0f / glControl.Width;
            var hScale = 1.0f / glControl.Height;

            var flippedRectSrc = new RectangleF(rectSrc.X, glControl.Height - rectSrc.Bottom, rectSrc.Width, rectSrc.Height);

            GL.TexCoord2(wScale * flippedRectSrc.X, hScale * (flippedRectSrc.Y + flippedRectSrc.Height)); GL.Vertex2(rectDst.X, rectDst.Y);
            GL.TexCoord2(wScale * (flippedRectSrc.X + flippedRectSrc.Width), hScale * (flippedRectSrc.Y + flippedRectSrc.Height)); GL.Vertex2(rectDst.X + rectDst.Width, rectDst.Y);
            GL.TexCoord2(wScale * (flippedRectSrc.X + flippedRectSrc.Width), hScale * flippedRectSrc.Y); GL.Vertex2(rectDst.X + rectDst.Width, rectDst.Y + rectDst.Height);
            GL.TexCoord2(wScale * flippedRectSrc.X, hScale * (flippedRectSrc.Y)); GL.Vertex2(rectDst.X, rectDst.Y + rectDst.Height);

            GL.End();
        }

        private void glControl_KeyDown(object sender, KeyEventArgs e)
        {
            log.Debug($"keyCode in keydown {(int)e.KeyCode}");
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Alt || e.KeyCode == Keys.ControlKey)
                return;

            string keys = Input.Encode((int)e.KeyCode);
            log.Debug($"keys after encode {keys}");
            if (keys != null)
                _neovim.vim_input(keys);

            e.Handled = true;
        }

        //private async Task HandleKeyInput(Stream inputStream)
        //{
        //    var task = Task.Run(() =>
        //    {
        //        using (inputStream)
        //        using (var streamReader = new StreamReader(inputStream))
        //        {
        //            while (!streamReader.EndOfStream)
        //            {
        //                Thread.Sleep(2000);
        //                var line = streamReader.ReadLine();
        //                var inputs = line.ToCharArray();

        //                string vimComand = "";
        //                foreach (var input in inputs)
        //                {
        //                    if (input == '\\')
        //                    {
        //                        vimComand += input.ToString();
        //                        continue;
        //                    }
        //                    else
        //                    {
        //                        vimComand += input.ToString();
        //                    }

        //                    //for control key
        //                    //<c-(?<key>\w)>
        //                    //

        //                    string pattern = @"\\(?<specialCharacter>\w)";
        //                    var match = Regex.Match(vimComand, pattern);
        //                    if (match.Success)
        //                    {
        //                        var suffix =   match.Groups["specialCharacter"].Value;
        //                        vimComand = GetSpecialCharacter(suffix);
        //                    }

        //                    _neovim.vim_input(vimComand);
        //                    vimComand = "";
        //                }
        //            }
        //        }
        //    });

        //    await task;
        //}


        private async Task HandleKeyInput(Stream inputStream)
        {
            var task = Task.Run(() =>
            {
                using (inputStream)
                using (var streamReader = new StreamReader(inputStream))
                {
                    while (!streamReader.EndOfStream)
                    {
                        Thread.Sleep(2000);
                        var line = streamReader.ReadLine();

                        //for control key
                        //<c-(?<key>\w)>
                        //


                        const string pattern = @"<(?<command>\w)-(?<key>\w)>";
                        var match = Regex.Match(line, pattern);
                        if (match.Success)
                        {
                            var command = match.Groups["command"].Value;
                            var key = match.Groups["key"].Value;
                        }


                        _neovim.vim_input(vimComand);
                        vimComand = "";
                    }
                }
            }
            });

            await task;
    }

    private string GetSpecialCharacter(string suffix)
    {
        switch (suffix)
        {
            case "r":
                return "\r";
            default:
                throw new InvalidOperationException(
                    $@"not support special character for \{suffix}");

        }

    }


    private async void LoadScriptToolStripMenuItem_Click(object sender, EventArgs e)
    {

        Stream myStream = null;
        var openFileDialog = new OpenFileDialog();

        openFileDialog.InitialDirectory = "c:\\";
        openFileDialog.Filter = "txt files (*.txt)|*.txt|All files (*.*)|*.*";
        openFileDialog.FilterIndex = 2;
        openFileDialog.RestoreDirectory = true;

        if (openFileDialog.ShowDialog() == DialogResult.OK)
        {
            myStream = openFileDialog.OpenFile();
            await HandleKeyInput(myStream);
        }
    }
}
}

﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Neovim;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using log4net;
using System.Linq;

namespace NeovimDemo
{
    public partial class MainWindow : Form
    {
        private readonly SynchronizationContext uiContext;
        private readonly NeovimClient neovim;
        private RectangleF cursor;
        private RectangleF scrollRegion;
        private readonly int columns = 55;
        private readonly int rows = 24;
        private float width;
        private float height;
        private FontGroup font;

        private readonly Color fgColor = Color.White;
        private Color bgColor = Color.DarkSlateGray;

        private FrameBuffer backBuffer;
        private FrameBuffer pingPongBuffer;
        private static readonly HashSet<char> shiftedSymbol =
            new HashSet<char>(new[] { '(', ')', '%', '{', '}', '$', '^' });

        private static readonly IDictionary<string, int> specialKeysLookup = new Dictionary<string, int>()
        {
            { "c", 0x11 }, // left control
            { "cr" , 0xD }, // carriage return
            { "esc", 0x1B }, // escape
            { "space", 0x20 }
        };

        // https://msdn.microsoft.com/en-us/library/windows/desktop/dd375731(v=vs.85).aspx
        private readonly IDictionary<string, int> commandCodes = new Dictionary<string, int>
        {
            { "c", 0x11}
        };

        private static readonly ILog log = LogManager.GetLogger(nameof(MainWindow));

        // Where to put vimrc ~/AppData/Local/nvim/init.vim
        // Install neovim from Chocolatey
        private static string neoVimPath = "C:/tools/neovim/Neovim/bin/nvim.exe";

        public MainWindow()
        {
            log.Debug("Enter a constructor");
            Text = "Codesanook Vim Animation";

            InitializeComponent();
            SuspendLayout();

            // glControl
            glControl = new GLControl
            {
                Font = new Font(
                    "Consolas",
                    15F,
                    FontStyle.Regular,
                    GraphicsUnit.Point,
                    0
                ),

                Location = new Point(0, 0),
                Margin = new Padding(10),
                Name = "glControl",
                TabIndex = 0,
                VSync = false,
                Dock = DockStyle.Fill
            };

            glControl.Load += new EventHandler(glControl_Load);
            glControl.Paint += new PaintEventHandler(glControl_Paint);
            glControl.KeyDown += new KeyEventHandler(glControl_KeyDown);

            mainPanel.Controls.Add(glControl);
            Name = "MainWindow";
            ResumeLayout(false);
            uiContext = SynchronizationContext.Current;

            neovim = new NeovimClient(neoVimPath);

            // Event is asynchronous so we need to handle the redraw event in the UI thread
            neovim.Redraw += (obj, args) => uiContext.Post(x => NeovimOnRedraw(obj, args), null);
            neovim.ui_attach(columns, rows, true);
        }

        private void InitialDimension()
        {
            font = new FontGroup(glControl.Font)
            {
                Color = fgColor
            };
            width = font.MonoSpaceWidth;
            height = font.LineSpacing;
            var columns = mainPanel.Width / (int)width;
            var rows = mainPanel.Height / (int)height;
        }

        private Color ColorFromRgb(long rgb)
        {
            var r = (byte)(rgb >> 16);
            var g = (byte)(rgb >> 8);
            var b = (byte)(rgb >> 0);
            return Color.FromArgb(r, g, b);
        }

        private void NeovimOnRedraw(object sender, NeovimRedrawEventArgs e)
        {
            bool shouldInvalidate = false;
            backBuffer.Bind();
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
                        if (rows == this.rows && columns == this.columns) return;

                        break;

                    case RedrawMethodType.UpdateForeground:
                        font.Color = ColorFromRgb(method.Params[0][0].AsInteger());
                        break;

                    case RedrawMethodType.UpdateBackground:
                        bgColor = ColorFromRgb(method.Params[0][0].AsInteger());
                        GL.ClearColor(bgColor);
                        break;

                    case RedrawMethodType.HighlightSet:
                        foreach (var arg in method.Params)
                        {
                            var dict = arg[0].AsDictionary();

                            foreach (var entry in dict)
                            {
                                var str = entry.Key.AsString(Encoding.Default);
                                if (str == "foreground")
                                    font.Color = ColorFromRgb(entry.Value.AsInteger());
                                else if (str == "background")
                                {
                                }
                                else if (str == "bold")
                                    if (entry.Value.AsBoolean())
                                        font.FontStyle |= FontStyle.Bold;
                                    else font.FontStyle &= ~FontStyle.Bold;
                                else if (str == "italic")
                                    if (entry.Value.AsBoolean())
                                        font.FontStyle |= FontStyle.Italic;
                                    else font.FontStyle &= FontStyle.Italic;
                            }
                        }
                        break;

                    case RedrawMethodType.EolClear:
                        shouldInvalidate = true;
                        DrawRectangle(new RectangleF(cursor.X, cursor.Y, this.columns * this.width - cursor.X, this.height), bgColor);
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
                        var tSize = font.Measure(text);

                        DrawRectangle(new RectangleF(cursor.Location, tSize), bgColor);

                        GL.Enable(EnableCap.Blend);
                        font.Print(text, new Vector2(cursor.X, cursor.Y));
                        GL.Disable(EnableCap.Blend);
                        GL.Color4(Color.White);

                        cursor.X += tSize.Width;
                        if (cursor.X >= this.columns * this.width) // Don't know if this is needed
                        {
                            cursor.X = 0;
                            cursor.Y += this.height;
                        }
                        break;

                    case RedrawMethodType.CursorGoto:
                        shouldInvalidate = true;
                        cursor.Y = method.Params[0][0].AsInteger() * this.height;
                        cursor.X = method.Params[0][1].AsInteger() * this.width;
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
                            srcRect = new RectangleF(scrollRegion.X, scrollRegion.Y + this.height, scrollRegion.Width,
                                scrollRegion.Height - this.height);
                            dstRect = new RectangleF(scrollRegion.X, scrollRegion.Y, scrollRegion.Width,
                                scrollRegion.Height - this.height);
                            clearRect = new RectangleF(scrollRegion.X, scrollRegion.Y + scrollRegion.Height - this.height,
                                scrollRegion.Width, this.height + 1);
                        }
                        // Scroll down
                        else if (count <= -1)
                        {
                            srcRect = new RectangleF(scrollRegion.X, scrollRegion.Y, scrollRegion.Width,
                                scrollRegion.Height - this.height);
                            dstRect = new RectangleF(scrollRegion.X, scrollRegion.Y + this.height, scrollRegion.Width,
                                scrollRegion.Height - this.height);
                            clearRect = new RectangleF(scrollRegion.X, scrollRegion.Y, scrollRegion.Width,
                                this.height + 1);
                        }

                        pingPongBuffer.Bind();
                        backBuffer.Texture.Bind();

                        DrawTexturedRectangle(srcRect, dstRect);

                        backBuffer.Bind();
                        pingPongBuffer.Texture.Bind();

                        DrawTexturedRectangle(dstRect, dstRect);

                        Texture2D.Unbind();

                        DrawRectangle(clearRect, bgColor);
                        break;

                    case RedrawMethodType.SetScrollRegion:
                        var x = method.Params[0][2].AsInteger() * this.width;
                        var y = method.Params[0][0].AsInteger() * this.height;
                        var width = (method.Params[0][3].AsInteger() + 1) * this.width;
                        var height = (method.Params[0][1].AsInteger() + 1) * this.height;

                        scrollRegion = new RectangleF(x, y, width, height);
                        break;

                    case RedrawMethodType.ModeChange:
                        shouldInvalidate = true;
                        var mode = method.Params[0][0].AsString(Encoding.Default);
                        if (mode == "insert")
                            cursor.Width = this.width / 4;
                        else if (mode == "normal")
                            cursor.Width = this.width;
                        break;
                }
            }
            FrameBuffer.Unbind();
            if (shouldInvalidate)
                glControl.Invalidate();
        }

        private void glControl_Load(object sender, EventArgs e)
        {
            InitialDimension();

            glControl.Width = Convert.ToInt32(width * columns);
            glControl.Height = Convert.ToInt32(height * rows);
            cursor = new RectangleF(0, 0, width, height);

            backBuffer = new FrameBuffer(glControl.Width, glControl.Height);
            pingPongBuffer = new FrameBuffer(glControl.Width, glControl.Height);

            GL.MatrixMode(MatrixMode.Projection);
            GL.LoadIdentity();
            GL.Ortho(0, glControl.Width, glControl.Height, 0, -1, 1);
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            GL.MatrixMode(MatrixMode.Modelview);
            GL.LoadIdentity();

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.Lighting);
            GL.ClearColor(bgColor);
        }

        private void glControl_Paint(object sender, PaintEventArgs e)
        {
            backBuffer.Texture.Bind();
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
            DrawRectangle(cursor, Color.White);
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
            log.Debug($"keyCode in key down {(int)e.KeyCode}");
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Alt || e.KeyCode == Keys.ControlKey)
                return;

            string keys = Input.ToUnicodeKey((int)e.KeyCode);
            log.Debug($"keys after encode {keys}");
            if (keys != null)
                neovim.vim_input(keys);

            e.Handled = true;
        }

        private void ProcessCommands(string commands)
        {
            var keys = commands.ToArray();
            var commandText = new StringBuilder();
            var appendKey = false;
            foreach (var key in keys)
            {
                if (key == '<')
                {
                    appendKey = true;
                }

                // command break
                if (key == '>')
                {
                    commandText.Append(key);
                    ProcessCommandKey(commandText.ToString());

                    //reset 
                    commandText = new StringBuilder();
                    appendKey = false;
                    continue;
                }

                if (appendKey)
                {
                    commandText.Append(key);
                    continue;
                }

                var keyboardState = new byte[256];
                if (IsCombinedWithShift(key))
                {
                    const int shiftCode = 0XA0;
                    keyboardState[shiftCode] = 0x81;

                    const int dataLinkEscapeCode = 0x10;
                    keyboardState[dataLinkEscapeCode] = 0x81;
                }

                var virtualCode = Input.GetVirtualCodeFromCharacter(key);
                var unicodeInput = Input.Encode(virtualCode, keyboardState);
                neovim.vim_input(unicodeInput);
            }
        }

        private static bool IsCombinedWithShift(char key)
        {
            if (char.IsUpper(key))
            {
                return true;
            }
            return shiftedSymbol.Contains(key);
        }

        private void ProcessCommandKey(string line)
        {
            //?: means not capture group
            var commandRegex = new Regex(
                @"<(?<command>\w+)(?:-(?<combinedKey>\w))?>",
                RegexOptions.Compiled | RegexOptions.IgnoreCase
            );

            var match = commandRegex.Match(line);
            if (match.Success)
            {
                var command = match.Groups["command"].Value;
                var commandCode = specialKeysLookup[command];
                var combindedKeyValue = match.Groups["combinedKey"].Value;

                if (!string.IsNullOrEmpty(combindedKeyValue))
                {
                    var combinedKey = combindedKeyValue.ToCharArray()[0];
                    var combinedKeyCode = (int)char.ToUpper(combinedKey);

                    var keyboardState = new byte[256];
                    keyboardState[commandCode] = 0x81;

                    var unicodeInput = Input.Encode(combinedKeyCode, keyboardState);
                    neovim.vim_input(unicodeInput);
                }
                else
                {
                    var keyboardState = new byte[256];
                    var unicodeInput = Input.Encode(commandCode, keyboardState);
                    neovim.vim_input(unicodeInput);
                }
            }
        }

        private void MainWindow_Load(object sender, EventArgs e)
        {
            var font = new Font("Verdana", 12, FontStyle.Regular);
            gridViewCommand.DefaultCellStyle.Font = font;
            gridViewCommand.RowHeadersVisible = true;

            var rowTemplate = gridViewCommand.RowTemplate;
            rowTemplate.Height = font.Height + 10;
            rowTemplate.DefaultCellStyle.Padding = new Padding(10, 2, 10, 2);

            var column = new DataGridViewTextBoxColumn
            {
                HeaderText = "Commands",
                Name = "commands"
            };
            column.HeaderCell.Style.Font = font;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            gridViewCommand.Columns.Add(column);
        }

        private async void btnRunCommands_Click(object sender, EventArgs e)
        {
            //start with a normal mode 
            string keys = Input.ToUnicodeKey((int)Keys.Escape);
            neovim.vim_input(keys);

            btnRunCommands.Enabled = false;
            foreach (DataGridViewCell cell in gridViewCommand.SelectedCells)
            {
                cell.Selected = false;
            }
            await Task.Delay(1000);

            var defaultColor = gridViewCommand.DefaultCellStyle.BackColor;
            foreach (DataGridViewRow row in gridViewCommand.Rows)
            {
                var value = GetCellValue(row);
                if (string.IsNullOrWhiteSpace(value)) continue;

                row.DefaultCellStyle.BackColor = ColorTranslator.FromHtml("#F00");
                await Task.Delay(1000);
                ProcessCommands(value);
                await Task.Delay(2000);
                row.DefaultCellStyle.BackColor = defaultColor;
            }

            btnRunCommands.Enabled = true;
        }

        private string GetCellValue(DataGridViewRow row)
        {
            const string columnName = "commands";
            if (row?.Cells[columnName] == null) return null;
            if (row.Cells[columnName].Value == null) return null;
            return row.Cells[columnName].Value.ToString();
        }
    }
}


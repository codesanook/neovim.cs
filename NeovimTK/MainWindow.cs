﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MsgPack;
using Neovim;
using OpenTK;
using OpenTK.Graphics.OpenGL;
using OpenTK.Input;
using OpenTK.Platform.Windows;
using QuickFont;

namespace NeovimTK
{
    public partial class MainWindow : Form
    {
        private readonly SynchronizationContext _uiContext;
        private NeovimClient _neovim;

        private RectangleF _cursor;
        private int _rows = 24;
        private int _columns = 80;
        private float _width;
        private float _height;
        private QFont _font;
        private Color _fgColor;
        private Color _bgColor = Color.DarkSlateGray;

        private FrameBuffer _backBuffer;
        private char[] charBuffer;

        public MainWindow()
        {
            InitializeComponent();

            _uiContext = SynchronizationContext.Current;
            _neovim = new NeovimClient(@"C:\Program Files\Neovim\bin\nvim.exe");
            // Event is asynchronous so we need to handle the redraw event in the UI thread
            _neovim.Redraw += (o, args) => _uiContext.Post((x) => NeovimOnRedraw(o, args), null);
            _neovim.ui_attach(_columns, _rows, true);
        }

        private void NeovimOnRedraw(object sender, NeovimRedrawEventArgs e)
        {
            _backBuffer.Bind();
            foreach (var f in e.Functions)
            {
                var list = f.AsList();
                string function = list[0].AsString(Encoding.Default);

                IList<IList<MessagePackObject>> args = new List<IList<MessagePackObject>>();
                for (var i = 1; i < list.Count; i++)
                    args.Add(list[i].AsList());

                switch (function)
                {
                    case "clear":
                        GL.Clear(ClearBufferMask.ColorBufferBit);
                        break;
            //        case "resize":
            //            _term.Resize(args[0][1].AsInt32(), args[0][0].AsInt32());
            //            break;

            //        case "update_fg":
            //            break;

            //        case "update_bg":
            //            break;

            //        case "highlight_set":
            //            Color fg = Colors.White;
            //            bool bold = false;
            //            bool italic = false;

            //            foreach (var arg in args)
            //            {
            //                var dict = arg[0].AsDictionary();

            //                foreach (var entry in dict)
            //                {
            //                    var str = entry.Key.AsString(Encoding.Default);
            //                    if (str == "foreground")
            //                    {
            //                        uint c = entry.Value.AsUInt32();
            //                        byte r = (byte)(c >> 16);
            //                        byte g = (byte)(c >> 8);
            //                        byte b = (byte)(c >> 0);
            //                        fg = Color.FromRgb(r, g, b);
            //                    }
            //                    else if (str == "bold")
            //                        bold = entry.Value.AsBoolean();
            //                    else if (str == "italic")
            //                        italic = entry.Value.AsBoolean();

            //                }
            //            }

            //            _term.Highlight(fg, bold, italic);
            //            break;

                    case "eol_clear":
                        PaintRectangle(new RectangleF(_cursor.X, _cursor.Y, _columns * _width - _cursor.X, _height), _bgColor);
                        break;

                    case "set_title":
                        Text = args[0][0].AsString(Encoding.Default);
                        break;

                    case "put":
                        List<byte> bytes = new List<byte>();

                        foreach (var arg in args)
                            bytes.AddRange(arg[0].AsBinary());

                        var text = Encoding.Default.GetString(bytes.ToArray());
                        var tSize = _font.Measure(text);
                        GL.Disable(EnableCap.Blend);
                        PaintRectangle(new RectangleF(_cursor.Location, tSize), _bgColor);
                        QFont.Begin();
                        _font.Print(text, new Vector2(_cursor.X, _cursor.Y));
                        QFont.End();
                        _cursor.X += _width;
                        if (_cursor.X == _columns*_width)
                        {
                            _cursor.X = 0;
                            _cursor.Y += _height;
                        }
                        break;

                    case "cursor_goto":
                        _cursor.Y = args[0][0].AsInt32() * _height;
                        _cursor.X = args[0][1].AsInt32() * _width;
                        break;

            //        case "scroll":
            //            _term.Scroll(args[0][0].AsSByte());
            //            break;

            //        case "set_scroll_region":
            //            break;

            //        case "normal_mode":
            //            //UpdateTerminal((t) => t.TCursor.Width = t._cellWidth);
            //            break;

            //        case "insert_mode":
            //            //UpdateTerminal((t) => t.TCursor.Width = 3);
            //            break;

            //        case "busy_start":
            //            break;

            //        case "busy_stop":
            //            break;

            //        case "mouse_on":
            //            break;

            //        case "mouse_off":
            //            break;
                }
            }
            FrameBuffer.Unbind();
            glControl.Invalidate();
        }


        private void glControl_Load(object sender, EventArgs e)
        {
            _font = new QFont(glControl.Font);
            _font.Options.Monospacing = QFontMonospacing.Yes;
            _font.Options.Colour = Color.White;

            _width = _font.MonoSpaceWidth;
            _height = _font.LineSpacing;
            glControl.Width = Convert.ToInt32(_width * _columns);
            glControl.Height = Convert.ToInt32(_height * _rows);

            _cursor = new RectangleF(0, 0, _width, _height);

            _backBuffer = new FrameBuffer(glControl.Width, glControl.Height);
            GL.Ortho(0, glControl.Width, glControl.Height, 0, -1, 1);
            GL.Viewport(0, 0, glControl.Width, glControl.Height);

            GL.Disable(EnableCap.DepthTest);
            GL.ClearColor(_bgColor);
        }

        private void glControl_Paint(object sender, PaintEventArgs e)
        {
            GL.Clear(ClearBufferMask.ColorBufferBit);

            _backBuffer.Texture.Bind();

            GL.Begin(PrimitiveType.Quads);

            GL.TexCoord2(0, 1); GL.Vertex2(0, 0);
            GL.TexCoord2(1, 1); GL.Vertex2(glControl.Width, 0);
            GL.TexCoord2(1, 0); GL.Vertex2(glControl.Width, glControl.Height);
            GL.TexCoord2(0, 0); GL.Vertex2(0, glControl.Height);

            GL.End();

            Texture2D.Unbind();

            PaintRectangle(_cursor, Color.Green);

            glControl.SwapBuffers();
        }

        private void PaintRectangle(RectangleF rect, Color color)
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

        private void glControl_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Alt || e.KeyCode == Keys.ControlKey)
                return;

            string keys = Input.Encode((int)e.KeyCode);
            if (keys != null)
                _neovim.vim_input(keys);

            e.Handled = true;
        }
    }
}

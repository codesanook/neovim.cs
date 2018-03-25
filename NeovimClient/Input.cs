using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace Neovim
{
    public static class Input
    {

        private static readonly ILog log = LogManager.GetLogger(nameof(Input));

        private static readonly Dictionary<Key, string> InvisibleKeys = new Dictionary<Key, string>()
        {
            { Key.Escape, "<Esc>" },
            { Key.F1, "<F1>" },
            { Key.F2, "<F2>" },
            { Key.F3, "<F3>" },
            { Key.F4, "<F4>" },
            { Key.F5, "<F5>" },
            { Key.F6, "<F6>" },
            { Key.F7, "<F7>" },
            { Key.F8, "<F8>" },
            { Key.F9, "<F9>" },
            { Key.F10, "<F10>" },
            { Key.F11, "<F11>" },
            { Key.F12, "<F12>" },
            { Key.Back, "<BS>"},
            { Key.Tab, "<Tab>" },
            { Key.Enter, "<Enter>" },
            { Key.Up, "<Up>" },
            { Key.Space, "<Space>" },
            { Key.Left, "<Left>" },
            { Key.Down, "<Down>" },
            { Key.Right, "<Right>" }
        };


        [DllImport("user32.dll")]
        public static extern int ToUnicode(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
                [Out, MarshalAs(UnmanagedType.LPWStr, SizeParamIndex = 4)] StringBuilder pwszBuff, int cchbuf, uint wFlags);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, MapType uMapType);


        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);


        private enum MapType : uint
        {
            MAPVK_VK_TO_VSC = 0x0,
            MAPVK_VSC_TO_VK = 0x1,
            MAPVK_VK_TO_CHAR = 0x2,
            MAPVK_VSC_TO_VK_EX = 0x3
        }

        private static string KeyToUnicode(Key key)
        {
            char ch = '\0';
            //https://www.fileformat.info/info/unicode/char/000f/index.htm

            //Converts a WPF Key into a Win32 Virtual-Key.
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);

            //uCode is a virtual-key code and is translated into a scan code.
            //If it is a virtual-key code that does not distinguish between left- and right-hand keys,
            //the left-hand scan code is returned.
            //If there is no translation, the function returns 0.
            //https://msdn.microsoft.com/en-us/library/windows/desktop/ms646306(v=vs.85).aspx
            uint scanCode = MapVirtualKey((uint)virtualKey, MapType.MAPVK_VK_TO_VSC);

            byte[] keyboardState = new byte[256];
            //https://msdn.microsoft.com/en-us/library/windows/desktop/ms646299(v=vs.85).aspx
            GetKeyboardState(keyboardState);


            var stringBuilder = new StringBuilder(2);
            //https://msdn.microsoft.com/en-us/library/windows/desktop/ms646320(v=vs.85).aspx
            int result = ToUnicode((uint)virtualKey, scanCode, keyboardState, stringBuilder, stringBuilder.Capacity, 0);

            switch (result)
            {
                case -1://fail
                case 0://no translation
                    break;
                default:
                    //success
                    ch = stringBuilder[0];
                    break;
            }

            string keys = "";
            // Not a writable key
            if (ch == '\0')
            {
                if (InvisibleKeys.TryGetValue(key, out keys))
                    return keys;

                return null;
            }

            //https://en.wikipedia.org/wiki/C0_and_C1_control_codes
            //https://en.wikipedia.org/wiki/Shift_Out_and_Shift_In_characters
            return ch.ToString();
        }

        public static string Encode(Key key)
        {
            return KeyToUnicode(key);
        }

        public static string Encode(int key)
        {
            log.Debug($"encode key value {key}");
            var unicodeKey =  KeyToUnicode(KeyInterop.KeyFromVirtualKey(key));

            log.Debug($"unicode key value {unicodeKey}");
            return unicodeKey;
        }


        public static string Encode(int virtualKey, byte[] keyboardState)
        {
            //win 32 and win form has the same key
            //convert virtual key to scan code
            uint scanCode = MapVirtualKey((uint)virtualKey, MapType.MAPVK_VK_TO_VSC);
            var stringBuilder = new StringBuilder(2);
            //https://msdn.microsoft.com/en-us/library/windows/desktop/ms646320(v=vs.85).aspx
            int result = ToUnicode((uint)virtualKey, scanCode, keyboardState, stringBuilder, stringBuilder.Capacity, 0);

            char ch = '\0';
            switch (result)
            {
                case -1:
                case 0:
                    break;
                default:
                    ch = stringBuilder[0];
                    break;
            }

            var keys = "";
            // Not a writable key
            if (ch == '\0')
            {
                if (InvisibleKeys.TryGetValue((Key)virtualKey, out keys))
                    return keys;

                return null;
            }

            //https://en.wikipedia.org/wiki/C0_and_C1_control_codes
            //https://en.wikipedia.org/wiki/Shift_Out_and_Shift_In_characters
            return ch.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;

namespace KnxTunnelSS
{
    public class Logger
    {
        private static bool bDebug = false;

        public static int Debug
        {
            set { bDebug = value != 0; }
            get { return bDebug ? 1 : 0; }
        }

        public static void Log(string message, params object[] arg)
        {
            if( bDebug )
                CrestronConsole.PrintLine(message, arg);
        }
    }
}
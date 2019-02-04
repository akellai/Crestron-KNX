using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;

namespace KnxTunnelSS
{
    internal class String_Pacer
    {
        int nDelay;
        CTimer Timer;

        CMutex bMutex = new CMutex();

        public delegate void RxHandler(string data);
        public RxHandler OnReceive { set; get; }

        public delegate void TxHandler(string data);
        public RxHandler OnSend { set; get; }

        private Queue<string> SendQueue = new Queue<string>();
        private Queue<string> ReceiveQueue = new Queue<string>();

        public String_Pacer(int delay)
        {
            nDelay = delay;
            Timer = new CTimer(OnTimer, this, delay, delay);
        }

        void SendTelegramToKnx()
        {
            if( SendQueue.Count > 0)
            {
                bMutex.WaitForMutex();
                try
                {
                    string sout = SendQueue.Dequeue();
                    if ((!string.IsNullOrEmpty(sout)) && (OnSend != null))
                        OnSend(sout);
                }
                catch (Exception e)
                {
                    Logger.Log("SendTelegramToKnx: {0}", e.Message);
                }
                finally
                {
                    bMutex.ReleaseMutex();
                }
            }
        }

        void SendTelegramToController()
        {
            if (ReceiveQueue.Count > 0)
            {
                bMutex.WaitForMutex();
                try
                {
                    string sout = ReceiveQueue.Dequeue();
                    if (!string.IsNullOrEmpty(sout) && (OnReceive != null))
                    {
                        OnReceive(sout);
                    }
                }
                catch (Exception e)
                {
                    Logger.Log("SendTelegramToController: {0}", e.Message);
                }
                finally
                {
                    bMutex.ReleaseMutex();
                }
            }
        }

        void OnTimer(Object o)
        {
            SendTelegramToController();
            SendTelegramToKnx();
        }

        public void ClearTx()
        {
            bMutex.WaitForMutex();
            SendQueue.Clear();
            bMutex.ReleaseMutex();
        }

        public void EnqueueTX(SimplSharpString data)
        {
            string sdata = data.ToString();
            bMutex.WaitForMutex();

            foreach (string s in data.ToString().Split(';'))
            {
                if (!string.IsNullOrEmpty(s))
                {
                    SendQueue.Enqueue(s);
                }
            }
            bMutex.ReleaseMutex();
        }

        public void EnqueueRX(String s)
        {
            bMutex.WaitForMutex();
            ReceiveQueue.Enqueue(s);
            bMutex.ReleaseMutex();
        }
    }
}
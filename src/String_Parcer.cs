using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;

namespace KnxTunnelSS
{
    internal class String_Pacer
    {
        CTimer Timer;
        long m_delay;
        bool m_active = false;

        CMutex bMutex = new CMutex();

        public delegate void RxHandler(string data);
        public RxHandler OnSend { set; get; }

        private Queue<string> SendQueue = new Queue<string>();

        public String_Pacer(int delay)
        {
            m_delay = delay;
            Timer = new CTimer(OnTimer, Timeout.Infinite);
        }

        void OnTimer(Object o)
        {
            bMutex.WaitForMutex();
            if ((SendQueue.Count > 0) && (OnSend != null))
            {
                m_active = true;
                OnSend(SendQueue.Dequeue());
                Timer.Reset(m_delay);
            }
            else
                m_active = false;
            bMutex.ReleaseMutex();
        }

        public void ClearTx()
        {
            bMutex.WaitForMutex();
            SendQueue.Clear();
            bMutex.ReleaseMutex();
        }

        public void EnqueueTX(string data)
        {
            bMutex.WaitForMutex();
            foreach (string s in data.Split(';'))
            {
                if (!string.IsNullOrEmpty(s))
                {
                    SendQueue.Enqueue(s);
                }
            }
            if (!m_active)
                Timer.Reset(m_delay);
            bMutex.ReleaseMutex();
        }
    }
}
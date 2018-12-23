using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Crestron.SimplSharp;

namespace KnxTunnelSS
{
    internal class String_Pacer
    {
        int nChunk_Size;
        int nDelay;

        CTimer Timer;

        CMutex bMutex = new CMutex();

        public delegate void ChunkHandler(string data);
        public ChunkHandler OnChunk { set; get; }

        public delegate void SendItemHandler(string data);
        public ChunkHandler OnSendItem { set; get; }

        public int Chunk_Size { set { nChunk_Size = value; } get { return nChunk_Size; } }

        StringBuilder Buffer;
        StringBuilder SendData;

        public String_Pacer(int chunk_size, int delay)
        {
            nChunk_Size = chunk_size;
            nDelay = delay;

            Timer = new CTimer(OnTimer, this, delay, delay);
            Buffer = new StringBuilder();
            SendData = new StringBuilder();
        }

        public void AddData(SimplSharpString data)
        {
            bMutex.WaitForMutex();
            SendData.Append(data.ToString());
            bMutex.ReleaseMutex();
        }

        void SendChunk()
        {
            if (SendData.Length > 0)
            {
                string sout = SendData.ToString();
                int idx = sout.IndexOf(';');
                if( idx>0 )
                {
                    if (OnSendItem != null)
                        OnSendItem(sout.Substring(0,idx).Replace(";",""));
                    SendData.Remove(0, idx+1);
                }
            }
        }

        void OnTimer(Object o)
        {
            bMutex.WaitForMutex();
            if (Buffer.Length > 0)
            {
                try
                {
                    {
                        int l = (Buffer.Length > nChunk_Size) ? nChunk_Size : Buffer.Length;
                        for (int i = 0; i < l; i++)
                        {
                            if (Buffer[i] == ';')
                            {
                                l = i+1;
                                break;
                            }
                        }

                        String s = Buffer.ToString(0, l);
                        Buffer.Remove(0, l);
                        if (OnChunk != null)
                            OnChunk(s);
                    }
                }
                catch (Exception e)
                {
                    Logger.Log(e.Message);
                    Logger.Log(e.StackTrace);
                }
            }
            SendChunk();
            bMutex.ReleaseMutex();
        }

        public void Enqueue(String s)
        {
            bMutex.WaitForMutex();
            Buffer.Append(s);
            bMutex.ReleaseMutex();
        }
    }
}
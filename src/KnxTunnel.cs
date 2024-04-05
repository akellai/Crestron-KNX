using System;
using System.Text;
using Crestron.SimplSharp;                          				// For Basic SIMPL# Classes
using Crestron.SimplSharp.CrestronSockets;

namespace KnxTunnelSS
{
    public class KnxTunnel
    {
        private UDPServer client;
        private IPEndPoint localEndpoint;
        private IPEndPoint remoteEndpoint;
        private CTimer udpReadTimer;
        private CTimer stateRequestTimer;
        private const int stateRequestTimerInterval = 3000;    // check every 3 seconds - this makes disconnect detection reasonably fast

        private readonly object _sendDatagramLock = new object();
        private readonly object _txsequenceNumberLock = new object();
        private byte _txsequenceNumber;
        private byte _rxSequenceNumber;
        private byte[] myAddress;

        private readonly object keep_alive_lock = new object();
        private bool m_b_keep_alive = false;
        private const int buffersz = 1024;

        public bool Alive
        {
            get
            {
                bool bret = m_b_keep_alive;
                Alive = false;
                return bret;
            }
            set
            {
                CMonitor.Enter(keep_alive_lock);
                m_b_keep_alive = value;
                CMonitor.Exit(keep_alive_lock);
            }
        }

        String_Pacer Pacer;

        /// <summary>
        ///     Some KNX Routers/Interfaces might need this parameter defined, some need this to be 0x29.
        ///     Default: 0x00
        /// </summary>
        private byte ActionMessageCode = 0;

        public delegate void ConnectedHandler();
        public ConnectedHandler OnConnect { set; get; }

        public delegate void DisconnectedHandler();
        public DisconnectedHandler OnDisconnect { set; get; }

        public delegate void RxHandler(SimplSharpString data);
        public RxHandler OnRx { set; get; }

        public delegate void TxHandler(SimplSharpString data);
        public RxHandler OnTx { set; get; }


        internal byte ChannelId { get; set; }

        internal byte IncrementSequenceNumber()
        {
            byte bret;
            CMonitor.Enter(_txsequenceNumberLock);
            bret = _txsequenceNumber++;
            CMonitor.Exit(_txsequenceNumberLock);
            return bret;
        }

        internal void DecrementSingleSequenceNumber()
        {
            CMonitor.Enter(_txsequenceNumberLock);
            _txsequenceNumber--;
            CMonitor.Exit(_txsequenceNumberLock);
        }

        internal void ResetSequenceNumber()
        {
            CMonitor.Enter(_txsequenceNumberLock);
            _txsequenceNumber = 0x00;
            CMonitor.Exit(_txsequenceNumberLock);
        }

        public int Debug
        {
            set { Logger.Debug = value; }
            get { return Logger.Debug; }
        }

        /// <summary>
        /// SIMPL+ can only execute the default constructor. If you have variables that require initialization, please
        /// use an Initialize method
        /// </summary>
        public KnxTunnel()
        {
            Pacer = new String_Pacer(100);   // do not send/receive more than 10 telegrams/sec
            Pacer.OnSend = MyTxHandler;
            udpReadTimer = new CTimer(UdpRead, Timeout.Infinite);
            stateRequestTimer = new CTimer(StateRequest, Timeout.Infinite);
        }

        public static byte[] ToByteArray(String hexString)
        {
            byte[] retval = new byte[hexString.Length / 2];
            for (int i = 0; i < hexString.Length; i += 2)
                retval[i / 2] = Convert.ToByte(hexString.Substring(i, 2), 16);
            return retval;
        }

        internal void MyTxHandler(string data)
        {
            if (string.IsNullOrEmpty(data))
                return;

            Logger.Log("MySendItem: {0}", data);
            try
            {
                string[] sitems = data.Split(new char[] { ':' });
                if (sitems.Length == 3)
                {
                    string GA = sitems[0];
                    int len = int.Parse(sitems[1]);
                    byte[] val = ToByteArray(sitems[2]);
                    Logger.Log("MySendItem before CreateActionDatagram: {0}:{1}:{2}", GA, len, val.Length);
                    SendData(CreateActionDatagram(GA, len == 1, val));
                }
                else if (sitems.Length == 1)
                {
                    string GA = sitems[0];
                    Logger.Log("MySendItem before read request: {0}", GA);
                    SendData(CreateRequestStatusDatagram(GA));
                }
                else
                    Logger.Log("MySendItem invalid item: {0}", data);

            }
            catch(System.Exception e)
            {
                Logger.Log("MySendItem exception: {0}", e.Message);
            }
        }

        private IPEndPoint get_IPEndpoint(string address, int port)
        {
            Logger.Log("IPEndpoint {0}:{1}", address, port);
            IPAddress addr = IPAddress.Parse(address);
            return new IPEndPoint(addr, port);
        }

        private string getLocalIP()
        {
            return CrestronEthernetHelper.GetEthernetParameter(CrestronEthernetHelper.ETHERNET_PARAMETER_TO_GET.GET_CURRENT_IP_ADDRESS,
                CrestronEthernetHelper.GetAdapterdIdForSpecifiedAdapterType(EthernetAdapterType.EthernetLANAdapter));
        }

        public int Connect(String address, int target_port, int src_port, string myAddress)
        {
            Logger.Log("Connect({0},{1},{2},{3})", address, target_port, src_port, myAddress);

            if (client != null)
            {
                if (client.ServerStatus == SocketStatus.SOCKET_STATUS_CONNECTED)
                    DisconnectRequest();
                Disconnect();
            }

            SocketErrorCodes error = SocketErrorCodes.SOCKET_INVALID_STATE;
            try
            {
                this.myAddress = KnxHelper.GetAddress(myAddress);
                remoteEndpoint = get_IPEndpoint(address, target_port);

                if( src_port<=0 )
                    src_port = target_port;
                localEndpoint = get_IPEndpoint(getLocalIP(), src_port);

                client = new UDPServer(remoteEndpoint, src_port, buffersz, EthernetAdapterType.EthernetLANAdapter);

                error = client.EnableUDPServer();
                if (error == SocketErrorCodes.SOCKET_OK)
                {
                    // start recieveing
                    //error = client.ReceiveDataAsync(udpReceiver);
                    //if (error == SocketErrorCodes.SOCKET_OPERATION_PENDING)
                    //{
                    error = ConnectRequest();
                    if (error != SocketErrorCodes.SOCKET_OK)
                    {
                        client.DisableUDPServer();
                        client = null;
                    }
                    else
                    {
                        udpReadTimer.Reset();
                        stateRequestTimer.Reset(stateRequestTimerInterval);
                    }
                }
            }
            catch (System.Exception e)
            {
                Logger.Log("Connect: Exception ({0})", e.Message);
                return -1;
            }

            Logger.Log("Connect: ({0})", error);
            return Convert.ToInt32(error);
        }

        public void Disconnect()
        {
            Logger.Log("Disconnect()");
            if (client != null)
            {
                udpReadTimer.Stop();
                stateRequestTimer.Stop();
                Alive = false;
                DisconnectRequest();
                client.DisableUDPServer();
                Pacer.ClearTx();
                client.Dispose();
                client = null;
                if (OnDisconnect != null)
                    OnDisconnect();
            }
        }

        public void Send(SimplSharpString data)
        {
            if (client != null)
            {
                Pacer.EnqueueTX(data.ToString());
            }
            else
                Logger.Log("Error: Send() to a Null client!");
        }

        // changed to synchronious receive to simplify the code
        public void UdpRead( object sender )
        {
            while (true)
            {
                if (client == null)
                    break;
                int len = client.ReceiveData();
                if (len > 0)
                {
                    Logger.Log("UdpRead: received {0} byte", len);
                    ProcessDatagram(client.IncomingDataBuffer);
                }
                else
                {
                    ErrorLog.Error("UdpReader: len {0}", len);
                    udpReadTimer.Stop();
                    break;
                }
            }
        }

        private void ProcessDatagram(byte[] datagram)
        {
            try
            {
                switch (KnxHelper.GetServiceType(datagram))
                {
                    case KnxHelper.SERVICE_TYPE.CONNECT_RESPONSE:
                        ProcessConnectResponse(datagram);
                        break;
                    case KnxHelper.SERVICE_TYPE.CONNECTIONSTATE_RESPONSE:
                        ProcessConnectionStateResponse(datagram);
                        break;
                    case KnxHelper.SERVICE_TYPE.DISCONNECT_REQUEST:
                        ProcessDisconnectRequest(datagram);
                        break;
                    case KnxHelper.SERVICE_TYPE.TUNNELLING_REQUEST:
                        ProcessDatagramHeaders(datagram);
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Log("ProcessDatagram: Exception ({0})", e.Message);
                ErrorLog.Exception("ProcessDatagram:", e);
            }
        }

        private void ProcessConnectResponse(byte[] datagram)
        {
            if (datagram[6] == 0x00 && datagram[7] == 0x24)
            {
                Logger.Log("ProcessConnectResponse: - No more connections available");
                ErrorLog.Error("ProcessConnectResponse - No more connections available");
                Disconnect();
            }
            else
            {
                ChannelId = datagram[6];
                ResetSequenceNumber();
                stateRequestTimer.Reset(stateRequestTimerInterval);
                Alive = true;
                if (OnConnect != null)
                    OnConnect();
            }
        }

        private void ProcessConnectionStateResponse(byte[] datagram)
        {
            var response = datagram[7];

            if (response == 0)
            {
                Alive = true;
                return;
            }

            if (response == 0x21)
                Logger.Log("ProcessConnectionStateResponse - No active connection with channel ID {0}", datagram[6]);
            else
                Logger.Log("ProcessConnectionStateResponse - Error code {0}", response);

            Disconnect();
        }

        private void StateRequest(object sender)
        {
            if (client == null)
                return;

            if (!Alive) // state response lost ?
            {
                ErrorLog.Error("StateRequest: no response to previous StateRequest, disconnect, socket status {0}:{1}",
                client.ServerStatus, client.DataAvailable);
                DisconnectRequest();
                Disconnect();
                return;
            }

            Logger.Log("StateRequest");

            byte[] datagram = {
                0x06, 0x10, 0x02, 0x07, 0x00,
                0x10, ChannelId, 0x00, 0x08, 0x01,
                localEndpoint.Address.GetAddressBytes()[0],
                localEndpoint.Address.GetAddressBytes()[1],
                localEndpoint.Address.GetAddressBytes()[2],
                localEndpoint.Address.GetAddressBytes()[3],
                (byte)(localEndpoint.Port >> 8),
                (byte)localEndpoint.Port
            };

            SendDatagram(datagram, datagram.Length);
            stateRequestTimer.Reset(stateRequestTimerInterval);
        }

        private SocketErrorCodes SendDatagram(byte[] data, int len)
        {
            if (client == null)
                return SocketErrorCodes.SOCKET_NOT_CONNECTED;

            CMonitor.Enter(_sendDatagramLock);
            SocketErrorCodes sret = client.SendData(data, len, remoteEndpoint);
            CMonitor.Exit(_sendDatagramLock);
            if (sret != SocketErrorCodes.SOCKET_OK)
            {
                ErrorLog.Error("SendDatagram: {0}", sret.ToString());
            }
            return sret;
        }

        private void ProcessDisconnectRequest(byte[] datagram)
        {
            if (datagram[7] != ChannelId)
            {
                return;
            }
            Disconnect();
        }

        private void ProcessDatagramHeaders(byte[] datagram)
        {
            if (datagram[7] != ChannelId)
                return;

            SendTunnelingAck(datagram[8]);
            ProcessCEMI(datagram);
        }

        public void SendTunnelingAck(byte sequenceNumber)
        {
            byte[] datagram =
            {
                0x06, 0x10, 0x04, 0x21, 0,
                0x0A, 0x04, ChannelId, sequenceNumber, 0
            };
            SendDatagram(datagram, datagram.Length);
        }

        private void BuildAndExecuteKnxRx(byte[] datagram)
        {
            int len = datagram[11];
            int dl = datagram[18 + len];
            string data = string.Empty;
            if (dl == 1)
            {
                int bitval = datagram[20 + len] & 0x3F;
                data = bitval.ToString("X2");
            }
            else
            {
                data = BitConverter.ToString(datagram, 21 + len, dl - 1).Replace("-", string.Empty);
            }

            string destination_address = KnxHelper.GetKnxDestinationAddressType(datagram[13 + len]).Equals(KnxHelper.KnxDestinationAddressType.INDIVIDUAL)
                        ? KnxHelper.GetIndividualAddress(new[] { datagram[16 + len], datagram[17 + len] })
                        : KnxHelper.GetGroupAddress(new[] { datagram[16 + len], datagram[17 + len] },
                        true);


            data = destination_address + ":" +
                dl + ":" + data;

            if (OnRx != null)
                OnRx(data);
        }

        protected void ProcessCEMI(byte[] datagram)
        {
            try
            {
                // CEMI
                // +--------+--------+--------+--------+----------------+----------------+--------+----------------+
                // |  Msg   |Add.Info| Ctrl 1 | Ctrl 2 | Source Address | Dest. Address  |  Data  |      APDU      |
                // | Code   | Length |        |        |                |                | Length |                |
                // +--------+--------+--------+--------+----------------+----------------+--------+----------------+
                //   1 byte   1 byte   1 byte   1 byte      2 bytes          2 bytes       1 byte      2 bytes
                //
                //  Message Code    = 0x11 - a L_Data.req primitive
                //      COMMON EMI MESSAGE CODES FOR DATA LINK LAYER PRIMITIVES
                //          FROM NETWORK LAYER TO DATA LINK LAYER
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          | Data Link Layer Primitive | Message Code | Data Link Layer Service | Service Description | Common EMI Frame |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |        L_Raw.req          |    0x10      |                         |                     |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |                           |              |                         | Primitive used for  | Sample Common    |
                //          |        L_Data.req         |    0x11      |      Data Service       | transmitting a data | EMI frame        |
                //          |                           |              |                         | frame               |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |        L_Poll_Data.req    |    0x13      |    Poll Data Service    |                     |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          |        L_Raw.req          |    0x10      |                         |                     |                  |
                //          +---------------------------+--------------+-------------------------+---------------------+------------------+
                //          FROM DATA LINK LAYER TO NETWORK LAYER
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          | Data Link Layer Primitive | Message Code | Data Link Layer Service | Service Description |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Poll_Data.con    |    0x25      |    Poll Data Service    |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |                           |              |                         | Primitive used for  |
                //          |        L_Data.ind         |    0x29      |      Data Service       | receiving a data    |
                //          |                           |              |                         | frame               |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Busmon.ind       |    0x2B      |   Bus Monitor Service   |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Raw.ind          |    0x2D      |                         |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |                           |              |                         | Primitive used for  |
                //          |                           |              |                         | local confirmation  |
                //          |        L_Data.con         |    0x2E      |      Data Service       | that a frame was    |
                //          |                           |              |                         | sent (does not mean |
                //          |                           |              |                         | successful receive) |
                //          +---------------------------+--------------+-------------------------+---------------------+
                //          |        L_Raw.con          |    0x2F      |                         |                     |
                //          +---------------------------+--------------+-------------------------+---------------------+

                //  Add.Info Length = 0x00 - no additional info
                //  Control Field 1 = see the bit structure above
                //  Control Field 2 = see the bit structure above
                //  Source Address  = 0x0000 - filled in by router/gateway with its source address which is
                //                    part of the KNX subnet
                //  Dest. Address   = KNX group or individual address (2 byte)
                //  Data Length     = Number of bytes of data in the APDU excluding the TPCI/APCI bits
                //  APDU            = Application Protocol Data Unit - the actual payload including transport
                //                    protocol control information (TPCI), application protocol control
                //                    information (APCI) and data passed as an argument from higher layers of
                //                    the KNX communication stack
                //
                if (datagram[10] != 0x29)
                    return;

                var type = datagram[20 + datagram[11]] >> 4;

                switch (type)
                {
                    case 8:
                        BuildAndExecuteKnxRx(datagram);
                        break;
                    case 4:
                        BuildAndExecuteKnxRx(datagram);
                        break;
                }
            }
            catch (Exception e)
            {
                Logger.Log("ProcessCEMI: {0}", e.Message);
            }
        }

        internal void DisconnectRequest()
        {
            // HEADER
            byte[] datagram = {
                0x06, 0x10, 0x02, 0x09, 0x00, 
                0x10, ChannelId, 0x00, 0x08, 0x01,
                localEndpoint.Address.GetAddressBytes()[0],
                localEndpoint.Address.GetAddressBytes()[1],
                localEndpoint.Address.GetAddressBytes()[2],
                localEndpoint.Address.GetAddressBytes()[3],
                (byte)(localEndpoint.Port >> 8),
                (byte)localEndpoint.Port
                };

            stateRequestTimer.Stop();
            SendDatagram(datagram, datagram.Length);
        }

        private SocketErrorCodes ConnectRequest()
        {
            byte[] datagram = {
                0x06, 0x10, 0x02, 0x05,
                0x00, 0x1A, 0x08, 0x01,
                localEndpoint.Address.GetAddressBytes()[0],
                localEndpoint.Address.GetAddressBytes()[1],
                localEndpoint.Address.GetAddressBytes()[2],
                localEndpoint.Address.GetAddressBytes()[3],
                (byte)(localEndpoint.Port >> 8),
                (byte)localEndpoint.Port,
                0x08, 0x01,
                localEndpoint.Address.GetAddressBytes()[0],
                localEndpoint.Address.GetAddressBytes()[1],
                localEndpoint.Address.GetAddressBytes()[2],
                localEndpoint.Address.GetAddressBytes()[3],
                (byte)(localEndpoint.Port >> 8),
                (byte)localEndpoint.Port,
                0x04, 0x04, 0x02, 0x00
                };

            SocketErrorCodes err = SendDatagram(datagram, datagram.Length);
            Logger.Log("ConnectRequest {0} {1} {2}", err,
                remoteEndpoint.Address, remoteEndpoint.Port);
            return err;
        }

        protected byte[] CreateActionDatagram(string destinationAddress, bool b_bit, byte[] data)
        {
            try
            {
                int dataLength = KnxHelper.GetDataLength(b_bit,data);
                byte[] totalLength = BitConverter.GetBytes(dataLength + 20);

                byte[] datagram = {
                    0x06, 0x10, 0x04, 0x20,
                    totalLength[1], totalLength[0],
                    0x04, ChannelId, IncrementSequenceNumber(), 0x00
                };

                return CreateActionDatagramCommon(destinationAddress, b_bit, data, datagram);
            }
            catch
            {
                DecrementSingleSequenceNumber();
                return null;
            }
        }

        protected byte[] CreateActionDatagramCommon(string destinationAddress, bool b_bit, byte[] data, byte[] header)
        {
            int i;
            var dataLength = KnxHelper.GetDataLength(b_bit,data);

            // HEADER
            var datagram = new byte[dataLength + 10 + header.Length];
            for (i = 0; i < header.Length; i++)
                datagram[i] = header[i];

            // CEMI (start at position 6)
            // +--------+--------+--------+--------+----------------+----------------+--------+----------------+
            // |  Msg   |Add.Info| Ctrl 1 | Ctrl 2 | Source Address | Dest. Address  |  Data  |      APDU      |
            // | Code   | Length |        |        |                |                | Length |                |
            // +--------+--------+--------+--------+----------------+----------------+--------+----------------+
            //   1 byte   1 byte   1 byte   1 byte      2 bytes          2 bytes       1 byte      2 bytes
            //
            //  Message Code    = 0x11 - a L_Data.req primitive
            //      COMMON EMI MESSAGE CODES FOR DATA LINK LAYER PRIMITIVES
            //          FROM NETWORK LAYER TO DATA LINK LAYER
            //          +---------------------------+--------------+-------------------------+---------------------+------------------+
            //          | Data Link Layer Primitive | Message Code | Data Link Layer Service | Service Description | Common EMI Frame |
            //          +---------------------------+--------------+-------------------------+---------------------+------------------+
            //          |        L_Raw.req          |    0x10      |                         |                     |                  |
            //          +---------------------------+--------------+-------------------------+---------------------+------------------+
            //          |                           |              |                         | Primitive used for  | Sample Common    |
            //          |        L_Data.req         |    0x11      |      Data Service       | transmitting a data | EMI frame        |
            //          |                           |              |                         | frame               |                  |
            //          +---------------------------+--------------+-------------------------+---------------------+------------------+
            //          |        L_Poll_Data.req    |    0x13      |    Poll Data Service    |                     |                  |
            //          +---------------------------+--------------+-------------------------+---------------------+------------------+
            //          |        L_Raw.req          |    0x10      |                         |                     |                  |
            //          +---------------------------+--------------+-------------------------+---------------------+------------------+
            //          FROM DATA LINK LAYER TO NETWORK LAYER
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          | Data Link Layer Primitive | Message Code | Data Link Layer Service | Service Description |
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          |        L_Poll_Data.con    |    0x25      |    Poll Data Service    |                     |
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          |                           |              |                         | Primitive used for  |
            //          |        L_Data.ind         |    0x29      |      Data Service       | receiving a data    |
            //          |                           |              |                         | frame               |
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          |        L_Busmon.ind       |    0x2B      |   Bus Monitor Service   |                     |
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          |        L_Raw.ind          |    0x2D      |                         |                     |
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          |                           |              |                         | Primitive used for  |
            //          |                           |              |                         | local confirmation  |
            //          |        L_Data.con         |    0x2E      |      Data Service       | that a frame was    |
            //          |                           |              |                         | sent (does not mean |
            //          |                           |              |                         | successful receive) |
            //          +---------------------------+--------------+-------------------------+---------------------+
            //          |        L_Raw.con          |    0x2F      |                         |                     |
            //          +---------------------------+--------------+-------------------------+---------------------+

            //  Add.Info Length = 0x00 - no additional info
            //  Control Field 1 = see the bit structure above
            //  Control Field 2 = see the bit structure above
            //  Source Address  = 0x0000 - filled in by router/gateway with its source address which is
            //                    part of the KNX subnet
            //  Dest. Address   = KNX group or individual address (2 byte)
            //  Data Length     = Number of bytes of data in the APDU excluding the TPCI/APCI bits
            //  APDU            = Application Protocol Data Unit - the actual payload including transport
            //                    protocol control information (TPCI), application protocol control
            //                    information (APCI) and data passed as an argument from higher layers of
            //                    the KNX communication stack
            //

            datagram[i++] =
                ActionMessageCode != 0x00
                    ? ActionMessageCode
                    : (byte)0x11;

            datagram[i++] = 0x00;
            datagram[i++] = 0xAC;

            datagram[i++] =
                KnxHelper.IsAddressIndividual(destinationAddress)
                    ? (byte)0x50
                    : (byte)0xF0;

            datagram[i++] = myAddress[0];   // Source address
            datagram[i++] = myAddress[1];
            var dst_address = KnxHelper.GetAddress(destinationAddress);
            datagram[i++] = dst_address[0];
            datagram[i++] = dst_address[1];
            datagram[i++] = (byte)dataLength;
            datagram[i++] = 0x00;
            datagram[i] = 0x80;

            KnxHelper.WriteData(datagram, b_bit, data, i);

            return datagram;
        }

        public void SendData(byte[] datagram)
        {
            SendDatagram(datagram, datagram.Length);
        }

        private byte[] CreateRequestStatusDatagram(string destinationAddress)
        {
            try
            {
                // HEADER
                var datagram = new byte[21];
                datagram[00] = 0x06;
                datagram[01] = 0x10;
                datagram[02] = 0x04;
                datagram[03] = 0x20;
                datagram[04] = 0x00;
                datagram[05] = 0x15;

                datagram[06] = 0x04;
                datagram[07] = ChannelId;
                datagram[08] = IncrementSequenceNumber();
                datagram[09] = 0x00;

                datagram[10] =
                    ActionMessageCode != 0x00
                        ? ActionMessageCode
                        : (byte)0x11;

                datagram[11] = 0x00;
                datagram[12] = 0xAC;

                datagram[13] =
                    KnxHelper.IsAddressIndividual(destinationAddress)
                        ? (byte)0x50
                        : (byte)0xF0;

                datagram[14] = myAddress[0];
                datagram[15] = myAddress[1];
                byte[] dst_address = KnxHelper.GetAddress(destinationAddress);
                datagram[16] = dst_address[0];
                datagram[17] = dst_address[1];

                datagram[18] = 0x01;
                datagram[19] = 0x00;
                datagram[20] = 0x00;

                return datagram;
            }
            catch
            {
                DecrementSingleSequenceNumber();
                return null;
            }
        }
    }
}

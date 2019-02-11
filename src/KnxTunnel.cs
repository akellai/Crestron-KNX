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
            Pacer.OnReceive = MyRxHandler;
            Pacer.OnSend = MyTxHandler;
            udpReadTimer = new CTimer(UdpRead, Timeout.Infinite);
            stateRequestTimer = new CTimer(StateRequest, Timeout.Infinite);
        }

        internal void MyRxHandler(string data)
        {
            if (OnRx != null)
                OnRx(new SimplSharpString(data));
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
                    Logger.Log("MySendItem before Action: {0}:{1}:{2}", GA, len, val.Length );
                    Action(GA, len==1, val);
                }
                else if (sitems.Length == 1)
                {
                    string GA = sitems[0];
                    Logger.Log("MySendItem before read request: {0}", GA);
                    RequestStatus(GA);
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
                {
                    DisconnectRequest();
                    client.DisableUDPServer();
                    client = null;
                }
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

                InitialParametersClass.ReadInInitialParameters(1);

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
                stateRequestTimer.Stop();
                Alive = false;
                DisconnectRequest();
                client.DisableUDPServer();
                Pacer.ClearTx();
                client = null;
                if (OnDisconnect != null)
                    OnDisconnect();
            }
        }

        public void Send(SimplSharpString data)
        {
            if (client != null)
            {
                Pacer.EnqueueTX(data);
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
                    Alive = true;
                    byte[] datagram = client.IncomingDataBuffer;
                    ProcessDatagram(datagram);
                }
                else
                {
                    ErrorLog.Error("UdpReader: len {0}", len);
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
            // HEADER
            var knxDatagram = new KnxDatagram
            {
                header_length = datagram[0],
                protocol_version = datagram[1],
                service_type = new[] { datagram[2], datagram[3] },
                total_length = datagram[4] + datagram[5],
                channel_id = datagram[6],
                status = datagram[7]
            };

            if (knxDatagram.channel_id == 0x00 && knxDatagram.status == 0x24)
            {
                Logger.Log("ProcessConnectResponse: - No more connections available");
                ErrorLog.Error("ProcessConnectResponse - No more connections available");
                Disconnect();
            }
            else
            {
                ChannelId = knxDatagram.channel_id;
                ResetSequenceNumber();
                stateRequestTimer.Reset(stateRequestTimerInterval);
                if (OnConnect != null)
                    OnConnect();
            }
        }

        private void ProcessConnectionStateResponse(byte[] datagram)
        {
            // HEADER
            // 06 10 02 08 00 08 -- 48 21
            var knxDatagram = new KnxDatagram
            {
                header_length = datagram[0],
                protocol_version = datagram[1],
                service_type = new[] { datagram[2], datagram[3] },
                total_length = datagram[4] + datagram[5],
                channel_id = datagram[6]
            };

            var response = datagram[7];

            if (response != 0x21)
                return;

            Logger.Log("ProcessConnectionStateResponse - No active connection with channel ID {0}", knxDatagram.channel_id);
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
                Disconnect();
                return;
            }

            // HEADER
            var datagram = new byte[16];
            Logger.Log("StateRequest");

            datagram[00] = 0x06;
            datagram[01] = 0x10;
            datagram[02] = 0x02;
            datagram[03] = 0x07;
            datagram[04] = 0x00;
            datagram[05] = 0x10;

            datagram[06] = ChannelId;
            datagram[07] = 0x00;
            datagram[08] = 0x08;
            datagram[09] = 0x01;
            datagram[10] = localEndpoint.Address.GetAddressBytes()[0];
            datagram[11] = localEndpoint.Address.GetAddressBytes()[1];
            datagram[12] = localEndpoint.Address.GetAddressBytes()[2];
            datagram[13] = localEndpoint.Address.GetAddressBytes()[3];
            datagram[14] = (byte)(localEndpoint.Port >> 8);
            datagram[15] = (byte)localEndpoint.Port;

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
            var channelId = datagram[7];
            if (channelId != ChannelId)
            {
                return;
            }
            Disconnect();
        }

        private void ProcessDatagramHeaders(byte[] datagram)
        {
            // HEADER
            // TODO: Might be interesting to take out these magic numbers for the datagram indices
            var knxDatagram = new KnxDatagram
            {
                header_length = datagram[0],
                protocol_version = datagram[1],
                service_type = new[] { datagram[2], datagram[3] },
                total_length = datagram[4] + datagram[5]
            };

            var channelId = datagram[7];
            if (channelId != ChannelId)
            {
                return;
            }

            var sequenceNumber = datagram[8];
            _rxSequenceNumber = sequenceNumber;
            var cemi = new byte[datagram.Length - 10];
            Array.Copy(datagram, 10, cemi, 0, datagram.Length - 10);

            SendTunnelingAck(sequenceNumber);
            ProcessCEMI(knxDatagram, cemi);
        }

        public void SendTunnelingAck(byte sequenceNumber)
        {
            // HEADER
            var datagram = new byte[10];
            datagram[00] = 0x06;
            datagram[01] = 0x10;
            datagram[02] = 0x04;
            datagram[03] = 0x21;
            datagram[04] = 0x00;
            datagram[05] = 0x0A;

            datagram[06] = 0x04;
            datagram[07] = ChannelId;
            datagram[08] = sequenceNumber;
            datagram[09] = 0x00;

            SendDatagram(datagram, datagram.Length);
        }

        private void BuildAndExecuteKnxRx(KnxDatagram datagram)
        {
            string data = string.Empty;
            if (datagram.data_length == 1)
            {
                int bitval = datagram.apdu[1] & 0x3F;
                data = bitval.ToString("X2");
            }
            else
            {
                data = BitConverter.ToString(datagram.apdu, 2).Replace("-", string.Empty);
            }

            data = datagram.destination_address + ":" +
                datagram.data_length + ":" + data;

            Pacer.EnqueueRX(data);
        }

        protected void ProcessCEMI(KnxDatagram datagram, byte[] cemi)
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
                datagram.message_code = cemi[0];
                datagram.aditional_info_length = cemi[1];

                if (datagram.aditional_info_length > 0)
                {
                    datagram.aditional_info = new byte[datagram.aditional_info_length];
                    for (var i = 0; i < datagram.aditional_info_length; i++)
                    {
                        datagram.aditional_info[i] = cemi[2 + i];
                    }
                }

                datagram.control_field_1 = cemi[2 + datagram.aditional_info_length];
                datagram.control_field_2 = cemi[3 + datagram.aditional_info_length];
                datagram.source_address = KnxHelper.GetIndividualAddress(new[] { cemi[4 + datagram.aditional_info_length], cemi[5 + datagram.aditional_info_length] });

                datagram.destination_address =
                    KnxHelper.GetKnxDestinationAddressType(datagram.control_field_2).Equals(KnxHelper.KnxDestinationAddressType.INDIVIDUAL)
                        ? KnxHelper.GetIndividualAddress(new[] { cemi[6 + datagram.aditional_info_length], cemi[7 + datagram.aditional_info_length] })
                        : KnxHelper.GetGroupAddress(new[] { cemi[6 + datagram.aditional_info_length], cemi[7 + datagram.aditional_info_length] }, 
                        true);

                datagram.data_length = cemi[8 + datagram.aditional_info_length];
                datagram.apdu = new byte[datagram.data_length + 1];

                for (var i = 0; i < datagram.apdu.Length; i++)
                    datagram.apdu[i] = cemi[9 + i + datagram.aditional_info_length];

                datagram.data = KnxHelper.GetData(datagram.data_length, datagram.apdu);

                Logger.Log("-----------------------------------------------------------------------------------------------------");
                Logger.Log(BitConverter.ToString(cemi));
                Logger.Log("Event Header Length: " + datagram.header_length);
                Logger.Log("Event Protocol Version: " + datagram.protocol_version.ToString("x"));
                Logger.Log("Event Service Type: 0x" + BitConverter.ToString(datagram.service_type).Replace("-", string.Empty));
                Logger.Log("Event Total Length: " + datagram.total_length);

                Logger.Log("Event Message Code: " + datagram.message_code.ToString("x"));
                Logger.Log("Event Aditional Info Length: " + datagram.aditional_info_length);

                if (datagram.aditional_info_length > 0)
                    Logger.Log("Event Aditional Info: 0x" + BitConverter.ToString(datagram.aditional_info).Replace("-", string.Empty));

                Logger.Log("Event Control Field 1: " + Convert.ToString(datagram.control_field_1, 2));
                Logger.Log("Event Control Field 2: " + Convert.ToString(datagram.control_field_2, 2));
                Logger.Log("Event Source Address: " + datagram.source_address);
                Logger.Log("Event Destination Address: " + datagram.destination_address);
                Logger.Log("Event Data Length: " + datagram.data_length);
                Logger.Log("Event APDU: 0x" + BitConverter.ToString(datagram.apdu).Replace("-", string.Empty));
                Logger.Log("-----------------------------------------------------------------------------------------------------");

                if (datagram.message_code != 0x29)
                    return;

                var type = datagram.apdu[1] >> 4;

                switch (type)
                {
                    case 8:
                        BuildAndExecuteKnxRx(datagram);
                        Logger.Log("{0}", datagram.source_address);
                        break;
                    case 4:
                        BuildAndExecuteKnxRx(datagram);
                        Logger.Log("Device {0} status {1}", datagram.source_address, datagram.destination_address);
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
            byte[] datagram = new byte[16];
            datagram[00] = 0x06;
            datagram[01] = 0x10;
            datagram[02] = 0x02;
            datagram[03] = 0x09;
            datagram[04] = 0x00;
            datagram[05] = 0x10;

            datagram[06] = ChannelId;
            datagram[07] = 0x00;
            datagram[08] = 0x08;
            datagram[09] = 0x01;
            datagram[10] = localEndpoint.Address.GetAddressBytes()[0];
            datagram[11] = localEndpoint.Address.GetAddressBytes()[1];
            datagram[12] = localEndpoint.Address.GetAddressBytes()[2];
            datagram[13] = localEndpoint.Address.GetAddressBytes()[3];
            datagram[14] = (byte)(localEndpoint.Port >> 8);
            datagram[15] = (byte)localEndpoint.Port;

            stateRequestTimer.Stop();
            SendDatagram(datagram, datagram.Length);
        }

        private SocketErrorCodes ConnectRequest()
        {
            // HEADER
            byte[] datagram = new byte[26];
            datagram[00] = 0x06;
            datagram[01] = 0x10;
            datagram[02] = 0x02;
            datagram[03] = 0x05;
            datagram[04] = 0x00;
            datagram[05] = 0x1A;

            datagram[06] = 0x08;
            datagram[07] = 0x01;
            datagram[08] = localEndpoint.Address.GetAddressBytes()[0];
            datagram[09] = localEndpoint.Address.GetAddressBytes()[1];
            datagram[10] = localEndpoint.Address.GetAddressBytes()[2];
            datagram[11] = localEndpoint.Address.GetAddressBytes()[3];
            datagram[12] = (byte)(localEndpoint.Port >> 8);
            datagram[13] = (byte)localEndpoint.Port;
            datagram[14] = 0x08;
            datagram[15] = 0x01;
            datagram[16] = localEndpoint.Address.GetAddressBytes()[0];
            datagram[17] = localEndpoint.Address.GetAddressBytes()[1];
            datagram[18] = localEndpoint.Address.GetAddressBytes()[2];
            datagram[19] = localEndpoint.Address.GetAddressBytes()[3];
            datagram[20] = (byte)(localEndpoint.Port >> 8);
            datagram[21] = (byte)localEndpoint.Port;
            datagram[22] = 0x04;
            datagram[23] = 0x04;
            datagram[24] = 0x02;
            datagram[25] = 0x00;

            SocketErrorCodes err = SendDatagram(datagram, datagram.Length);
            Logger.Log("ConnectRequest {0} {1} {2}", err,
                remoteEndpoint.Address, remoteEndpoint.Port);
            return err;
        }

        public void Action(string destinationAddress, bool b_bit, byte[] data)
        {
            SendData(CreateActionDatagram(destinationAddress, b_bit, data));
        }

        public void RequestStatus(string destinationAddress)
        {
            SendData(CreateRequestStatusDatagram(destinationAddress));
        }

        protected byte[] CreateActionDatagram(string destinationAddress, bool b_bit, byte[] data)
        {
            try
            {
                var dataLength = KnxHelper.GetDataLength(b_bit,data);

                // HEADER
                var datagram = new byte[10];
                datagram[00] = 0x06;
                datagram[01] = 0x10;
                datagram[02] = 0x04;
                datagram[03] = 0x20;

                var totalLength = BitConverter.GetBytes(dataLength + 20);
                datagram[04] = totalLength[1];
                datagram[05] = totalLength[0];

                datagram[06] = 0x04;
                datagram[07] = ChannelId;
                datagram[08] = IncrementSequenceNumber();
                datagram[09] = 0x00;

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
            // 4 times???
            SendDatagram(datagram, datagram.Length);
            //client.SendData(datagram, datagram.Length, remoteEndpoint);
            //client.SendData(datagram, datagram.Length, remoteEndpoint);
            //client.SendData(datagram, datagram.Length, remoteEndpoint);
        }

        protected byte[] CreateRequestStatusDatagram(string destinationAddress)
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

                return CreateRequestStatusDatagramCommon(destinationAddress, datagram, 10);
            }
            catch
            {
                DecrementSingleSequenceNumber();
                return null;
            }
        }

        protected byte[] CreateRequestStatusDatagramCommon(string destinationAddress, byte[] datagram, int cemi_start_pos)
        {
            var i = 0;

            datagram[cemi_start_pos + i++] =
                ActionMessageCode != 0x00
                    ? ActionMessageCode
                    : (byte)0x11;

            datagram[cemi_start_pos + i++] = 0x00;
            datagram[cemi_start_pos + i++] = 0xAC;

            datagram[cemi_start_pos + i++] =
                KnxHelper.IsAddressIndividual(destinationAddress)
                    ? (byte)0x50
                    : (byte)0xF0;

            datagram[cemi_start_pos + i++] = myAddress[0];
            datagram[cemi_start_pos + i++] = myAddress[1];
            byte[] dst_address = KnxHelper.GetAddress(destinationAddress);
            datagram[cemi_start_pos + i++] = dst_address[0];
            datagram[cemi_start_pos + i++] = dst_address[1];

            datagram[cemi_start_pos + i++] = 0x01;
            datagram[cemi_start_pos + i++] = 0x00;
            datagram[cemi_start_pos + i] = 0x00;

            return datagram;
        }
    }
}

Crestron SIMPL# module to work natively with KNX IP Tunneling protocol
This allows working directly with generic IP KNX routers

The idea is to convert EIB telegrams to strings in a text format like "1/2/3:1:00;" and vice versa.
The ability to send read request is also added
- to send a bit value (switch on) - send the following serial value to the TX$: "1/2/3:1:01;"
- to send a byte value (dimming) - send the following serial value to the TX$: "1/2/4:2:FF;"
- to send a 2 byte value (temperature value, 30C) - send the following serial value to the TX$: "1/2/4:3:1977;"
- the received values are in the same format
- to send a read request for 1/2/3, send the following serial value: "1/2/3;"
- you can send multiple values in one serial, but keep in mind these will be sent with 50 msec interval: "1/2/3:1:01;1/2/4:2:FF;"
- do not forget to add the ; symbol as it is used as a separator

some random comments:
- Reconnect logic is not implemented in the module. It's easy to add with an oscillator (enabled as soon as 'Not Connected' signal goes high)
- Handling of 6 bit type differs from the original Knxlib.NET as I wanted to directly control the type of the telegram sent. Sorry for a misleading data length returned: 1 for 6 bit; 2 for 1 byte, 3 for 2 bytes etc
- If you have hundreds of KNX devices and heavy traffic, KnxSplitters can reduce the CPU load. Splitter can be used to split the TX according to the group. The implementation is very simple - the appropriate RX will get the serial signal as soon as the KNX string starts with the string in the parameter. For example you can subclass the groups into "1/", "2/", "3/" etc. and chain these with the groups like 1/1/, 1/2, 1/3 etc. I have >1000 KNX sensors and an average telegram ratio is approximately 1 per second (don't ask why). If using ~20 splitters I'm limiting the CPU load from 60% to ~17% on the RMC3 system. If the KnxTunnel is disconnected, the  load is ~13%
- based on Knx.NET https://github.com/lifeemotions/knx.net
- also based on https://github.com/DevinCook/SimplSharpNetUtils


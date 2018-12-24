Crestron SIMPL# module to work natively with KNX IP Tunneling protocol
This allows working directly with generic IP KNX routers

The idea is to convert EIB telegrams to strings in a text format like "1/2/3:1:00" and vuise versa.

some random comments:
- Reconnect logic is not implemeted in the module. It's easy to add with an oscillator (enabled as soon as 'Not Connected' signal goes high)
- Handling of 6 bit type differs from the original Knxlib.NET as I wanted to directly control the type of the telegram sent. Sorry for a misleading data lenght returned: 1 for 6 bit; 2 for 1 byte, 3 for 2 bytes etc
- If you have hundreds of KNX devices and heavy traffic, KnxSplitters can reduce the CPU load. Splitter can be used to split the TX according to the group. The implamantation is very simple - the appropriate RX will get the serial signal as soon as the KNX string starts with the string in the parameter. For example you can subclass the groups into "1/", "2/", "3/" etc. And chain these with the groups like 1/1/, 1/2, 1/3 etc. I have >1000 KNX sensors and an average telegram ration is approximately 1 per second. If using ~20 splitters I'm limiting the CPU load to ~17% (vs 60% without splitters) on the RMC3 system. If the KnxTunnel is disconnected, the  load is ~13%
- based on Knx.NET https://github.com/lifeemotions/knx.net
- also based on https://github.com/DevinCook/SimplSharpNetUtils

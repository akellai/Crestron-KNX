Crestron SIMPL# module to work natively with KNX IP Tunneling protocol
This allows working directly with generic IP KNX routers

Basic idea is to convert EIB telegram to string like "1/2/3:1:00" and send it to Crestron
Data from crestron is in the same format and is sent to the router
some random comments:
- Reconnect logic is not implemeted in the module. It's easy to do with an oscillator enabled with Not Connected sygnal
- Handling of 6 bit type differs from the original Knxlib.NET as I wanted to directly control the type of the telegram sent
- If you have hundreds of KNX devices and heavy traffic, KnxSplitters are recommended. I have >1000 sensors and telegrams coming in every second. This requires ~20 splitters to get CPU load to ~20% on the RMC3 system
- based on Knx.NET https://github.com/lifeemotions/knx.net
- also based on https://github.com/DevinCook/SimplSharpNetUtils

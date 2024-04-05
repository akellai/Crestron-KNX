Crestron SIMPL# module to work natively with KNX IP Tunneling protocol
This allows working directly with generic IP KNX routers

The idea is to convert EIB telegrams to strings in a text format like "1/2/3:1:00" and vice versa.
- to send a bit value (switch on) - send the following serial value to the TX$: "1/2/3:1:01"
- to send a byte value (dimming) - send the following serial value to the TX$: "1/2/4:2:FF"
- to send a 2 byte value (temperature value, 30C) - send the following serial value to the TX$: "1/2/4:3:1977"
- the received values are in the same format
- to send a read request for 1/2/3, send the following serial value: "1/2/3"
- you can combine multiple values in one serial, but keep in mind these will be sent with 100 msec interval: "1/2/3:1:01;1/2/4:2:FF"
- if sending multiple values, use the ‘;’ symbol as a separator

- Reconnect logic is not implemented in the module. It's easy to add with an oscillator (enabled as soon as 'Not Connected' signal goes high)
- The original Knxlib.NET is no longer active. I had to simplify the code/fix couple of bugs.
- The data length returned: 1 for 6 bit; 2 for 1 byte, 3 for 2 bytes etc
- If you have hundreds of KNX devices and heavy traffic, KnxSplitters reduce the CPU load. Without the splitter the controller would need to trigger hundreds of SIMPL+ modules on a single signal wave. Due to Crestron implementation this would result in many context switches and would stress the controller. Splitter helps to avoid this problem by splitting the RX according to the group. The implementation is very simple - the appropriate RX will get the serial signal as soon as the KNX string starts with the string in the parameter. For example, you can subclass the groups into "1/", "2/", "3/" etc. and chain these with the groups like 1/1/, 1/2, 1/3 etc. I have >1000 KNX sensors and an average telegram ratio is approximately 1 per second (don't ask why). If using ~20 splitters I'm limiting the CPU load from 60% to ~17% on the RMC3 system. If the KnxTunnel is disconnected, the load is ~13%

- based on Knx.NET https://github.com/lifeemotions/knx.net
- also based on https://github.com/DevinCook/SimplSharpNetUtils

Series 4 controllers (April 2024):
More code simplification is done to get rid of memory leaks under 4-series controllers. No leaks occur on RMC3/CP3 but ~3.8 MB/day leaks on RMC4. I assume the leaks are b/c of the bugs in current 4 series firmware (might be fixed in the future). The updated module works well under both 3 and current 4 series without the need to wait for the firmware fixes. It's also possible (easy) to convert/compile specifically for 4-series (tested and it works, but for now I do not see any significant advantage of 4-series specific module)

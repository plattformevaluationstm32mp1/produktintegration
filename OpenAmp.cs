using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Vat.Atk.Gateway.Sensor.CanFd
{
    public struct CanMessage
    {
        public uint receiverId = 0;
        public uint messageId = 0;
        public uint senderId = 0;
        public CanId canId = default!;
        public long systemTimeStamp100ns = 0;
        public int frameLength = 0;
        public byte[] data = new byte[64];

        public CanMessage() { }
    }

    public struct SensorRxInfo
    {
        public int sensorUid;
        public uint canReceiverId;
        public Channel<CanMessage> channel;
    }

    public class OpenAmp
    {
        private readonly Thread? _canFdReaderThread = null;
        private readonly string _canInterface;
        private long _busLoadRxData = 0;
        private long _busLoadLastTicks = DateTimeOffset.UtcNow.Ticks;
        private SensorValidatorVtsn _validator1 = new SensorValidatorVtsn(1);
        private SensorValidatorVtsn _validator2 = new SensorValidatorVtsn(2);
        readonly System.Diagnostics.Stopwatch s = new System.Diagnostics.Stopwatch();
        readonly ConcurrentDictionary<string, SensorRxInfo> Sensors = new ConcurrentDictionary<string, SensorRxInfo>();

        /// <summary>
        /// For debug purposes: print the whole received can data 
        ///</summary>
        private void PrintRawCanData(CanId canId, CanMessage canMessage)
        {
            /*only for debugoutput of can data*/
            Span<byte> data = new Span<byte>(canMessage.data, 0, canMessage.frameLength);
            string type = canId.ExtendedFrameFormat ? "EFF" : "SFF";
            string dataAsHex = string.Join(string.Empty, data.ToArray().Select((x) => x.ToString("X2")));
            Console.WriteLine($"Id: 0x{canId.Value:X2} [{type}]: {dataAsHex}");
        }

        /// <summary>
        /// For debug purposes: print the actual bus load based on the received data
        ///</summary>
        private void PrintBusLoad(int newData)
        {
            _busLoadRxData = _busLoadRxData + newData;

            /*update bus load every 5s*/
            long actualTicks = DateTimeOffset.UtcNow.Ticks;
            long passed1ms = (actualTicks - _busLoadLastTicks) / TimeSpan.TicksPerMillisecond;
            if (passed1ms > 5000)
            {
                _busLoadLastTicks = actualTicks;
                Console.WriteLine("Bus load {0}: {1} KB/s", _canInterface, _busLoadRxData / passed1ms);
                _busLoadRxData = 0;
            }
        }

        /// <summary>
        /// Thread for rreading the CAN data from "/dev/ttyRPMSG1"
        ///</summary>
        private void OpenAmpReader()
        {
            const string RPMSG_DEV = "/dev/ttyRPMSG1";
            long startTime100ns = DateTimeOffset.UtcNow.Ticks;
            try
            {
                // Open the RPMsg UART device file
                using (FileStream fileStream = new FileStream(RPMSG_DEV, FileMode.Open))
                using (StreamReader streamReader = new StreamReader(fileStream))
                {
                    // Read lines from the device file
                    while (true)
                    {
                        string line = streamReader.ReadLine();
                        if (line == null)
                            break;
                        
                        CanMessage canMessage = new CanMessage();
                        
                        try
                        {
                            //split everything in seperated fields with space as separator
                            char[] separators = { ' ' };
                            string[] fields = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);
                            if (fields.Length > 0)
                            {
                                //field 1: this is the time offset --> but use actual time for timestamp 
                                canMessage.systemTimeStamp100ns = DateTimeOffset.UtcNow.Ticks;
                                
                                //field 3: CanId
                                canMessage.canId.Raw = uint.Parse(fields[3], System.Globalization.NumberStyles.HexNumber);
                                canMessage.receiverId = canMessage.canId.Value & 0x000F;          //receiver id: bit 3..0
                                canMessage.messageId = (canMessage.canId.Value & 0x01F0) >> 4;    //message id: bit 9..4
                                canMessage.senderId = (canMessage.canId.Value & 0x0300) >> 9;     //message id: bit 11..10 

                                //field 5: data length
                                canMessage.frameLength = int.Parse(fields[5]);

                                //field 6: can data
                                for (int i = 0; i < canMessage.frameLength; i++)
                                {
                                    canMessage.data[i] = byte.Parse(fields[6 + i], System.Globalization.NumberStyles.HexNumber);
                                }

                                //optional for debugging
                                PrintRawCanData(canMessage.canId, canMessage);
                            }
                        }
                        catch (FormatException ex)
                        {
                            Console.Error.WriteLine("Format error of the received data: " + ex.Message);
                        }

                        // check for message ID "Sensor data telegram" and send the telegram to the maching sensor
                        if (canMessage.messageId == 0x0010)
                        {
                            foreach (SensorRxInfo sensorRxInfo in Sensors.Values)
                            {
                                if (canMessage.receiverId == sensorRxInfo.canReceiverId)
                                {
                                    ChannelWriter<CanMessage> canChannelWriter = sensorRxInfo.channel.Writer;
                                    canChannelWriter.TryWrite(canMessage);
                                }
                            }
                        }
                        else
                        {
                            //s_logger.LogWarning($"Invalid CAN frame received!");
                            PrintRawCanData(canMessage.canId, canMessage);
                        }
                    }
                }
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine("Failed to read data from RPMsg UART device file: " + ex.Message);
            }
        }

        public OpenAmp(string canInterface)
        {
            _canInterface = canInterface;
            _canFdReaderThread = new Thread(OpenAmpReader);
            _canFdReaderThread.Start();
        }

        /// <summary>
        /// Attach a sensor
        ///</summary>
        public ChannelReader<CanMessage> AttachSensor(string sensorUid, uint receiverId)
        {
            Channel<CanMessage> rxChannel = Channel.CreateBounded<CanMessage>(
                new BoundedChannelOptions(1000)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleWriter = true
                });


            SensorRxInfo sensorRxInfo;
            sensorRxInfo.sensorUid = int.Parse(sensorUid);
            sensorRxInfo.canReceiverId = receiverId;
            sensorRxInfo.channel = rxChannel;
            Sensors.TryAdd(sensorUid, sensorRxInfo);

            return rxChannel.Reader;
        }

        /// <summary>
        /// Detach a sensor
        ///</summary>
        public void DetachSensor(string sensorUid)
        {

            Sensors.Clear();
        }
    }
}
using System.Text.Encodings;
using System.Net.Sockets;
using log4net;

namespace dvr_api
{
    public class Utils
    {
        protected static readonly ILog log = LogManager.GetLogger(typeof(Utils)); // logger!
        /*
        * 
        *
        *
        * Methods to facilitate working with the '$IDENTIFIER ; IEUF ; 239573GYB <CR>' DVR protocol
        * 
        * 
        * 
        */

        // Input:20230831-160440
        // formats DVR's response into c# DateTime obj 
        public static DateTime formatIntoDateTime(string datetime)
        {
            if (datetime.Length != 15)
            {
                throw new ArgumentException("Input string length should be 15 characters.");
            }

            // Parse the input string as a DateTime
            return DateTime.ParseExact(datetime, "yyyyMMdd-HHmmss", null);
        }

        // Output:20230831-160440
        // formats c# DateTime obj into DVR's format
        public static string formatFromDateTime(DateTime datetime)
        {
            return datetime.ToString("yyyyMMdd-HHmmss");
        }

        // sectioned by ';' only use when you want singular sections for whatever operation.
        public static string section(string message, int section)
        {
            return message.Split(';')[section].Trim();
        }

        public static string getMDVRIdFromMessage(string message)
        {
            int maxPosIdCanBe = 2;
            if (!string.IsNullOrEmpty(message))
            {
                string[] arr = message.Split(';', StringSplitOptions.TrimEntries);
                if (arr.Length < 2) { return null; } // if smaller than smalest possible message like: $VIDEO;123456 then return null.
                for (int i=0; i <= maxPosIdCanBe; i++)
                {
                    if (arr[i].All(char.IsAsciiDigit))
                    {
                        return arr[i];
                    }
                }
            }
            return null;
        }
        
        /*
         * 
         * REQ MATCH: device id, start time, length. (for video requests anyway.)
         * 
         */

        // best to have in one place. Complicated name to avoid confusion.
        // $FILE;[protocol];[DeviceID];[SN];[camera];[RStart];[Rlen];[VStart];[Vlen];[file len]<CR>[bytes]
        public static string getReqMatchStringFromFilePacketHeader(string header)
        {
            string[] split_header = header.Split(';', StringSplitOptions.TrimEntries);
            return split_header[2] + split_header[5] + split_header[6];
        }

        // best to have in one place. Complicated name to avoid confusion.
        // $VIDEO;[DeviceID];[type];[camera];[start];[time length]<CR>
        public static string getReqMatchStringFromVideoPacketHeader(string header)
        {
            string[] split_header = header.Split(';', StringSplitOptions.TrimEntries);
            return split_header[1] + split_header[4] + split_header[5];
        }

        public static byte[] replaceByteSequence(byte[] input, byte[] sequence, byte[] replacement)
        {
            List<byte> result = new List<byte>();
            int i;

            for (i = 0; i <= input.Length - sequence.Length; i++)
            {
                bool foundMatch = true;
                for (int j = 0; j < sequence.Length; j++)
                {
                    if (input[i + j] != sequence[j])
                    {
                        foundMatch = false;
                        break;
                    }
                }
                if (foundMatch)
                {
                    result.AddRange(replacement);
                    i += sequence.Length - 1;
                }
                else
                {
                    result.Add(input[i]);
                }
            }
            for (; i < input.Length; i++)
            {
                result.Add(input[i]);
            }
            return result.ToArray();
        }
    }
}

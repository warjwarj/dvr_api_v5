using log4net;

using System.Net.Sockets;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;

/*
* 
*
*
* Methods to facilitate TCP communication
* 
* 
* 
*/

namespace dvr_api
{
    /// <summary>
    /// This class contains methods that facilitate and assist in tcp communication.<br/>
    /// Note: Any function with a name containing 'Message' assumes a line feed (\r) as the message delimiter.<br/>
    /// Note: All of these functions catch SocketException internally. If you want to rethrow just specify rethrowCaughtException as true.<br/>
    /// Note: None of these functions pass the given CancellationToken to the internal send/receive operation currently.<br/>
    /// </summary>
    public class TcpUtils
    {
        // reuse buffers for socket operations
        public static ArrayPool<byte> bufferPool = ArrayPool<byte>.Shared;

        // logger!
        protected static readonly ILog _logger = LogManager.GetLogger(typeof(TcpUtils));

        public static bool isWriteable(Socket socket) => socket.Poll(1000, SelectMode.SelectWrite);
        public static bool isReadable(Socket socket) => socket.Poll(1000, SelectMode.SelectRead);
        public static string GetBits(byte b) => Convert.ToString(b, 2).PadLeft(8, '0');

        /// <summary>
        /// 
        /// </summary>
        /// <param name="eArgs"></param>
        /// <returns>the amount of bytes into the buffer from 0 comprising the handshake bytes</returns>
        public static int PrepWSHandshake(ref SocketAsyncEventArgs eArgs)
        {
            var dh = (DataHolder)eArgs.UserToken;
            var client = (APIClient)dh.client;
            if (dh != null && client != null)
            {
                if (eArgs.BytesTransferred > 3)
                {
                    string data = Encoding.UTF8.GetString(eArgs.Buffer[eArgs.Offset..(eArgs.Offset + eArgs.BytesTransferred)]);
                    if (Regex.IsMatch(data, "^GET"))
                    {
                        /*
                        *  Obtain the value of the "Sec-WebSocket-Key" request header without any leading or trailing whitespace
                        *  Concatenate it with "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" (a special GUID specified by RFC 6455)
                        *  Compute SHA-1 and Base64 hash of the new value
                        *  Write the hash back as the value of "Sec-WebSocket-Accept" response header in an HTTP response
                        */
                        string websockkey = Convert.ToBase64String(
                            SHA1.Create().ComputeHash(
                                    Encoding.UTF8.GetBytes(
                                        new Regex("Sec-WebSocket-Key: (.*)", RegexOptions.IgnoreCase)
                                        .Match(data).Groups[1].Value.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
                                    )
                                )
                            );
                        const string eol = "\r\n"; // HTTP/1.1 defines the sequence CR LF as the end-of-line marker
                        string response = "HTTP/1.1 101 Switching Protocols" + eol +
                            "Connection: Upgrade" + eol +
                            "Upgrade: websocket" + eol +
                            "Sec-WebSocket-Accept: " + websockkey + eol + eol;
                        Array.Clear(eArgs.Buffer, eArgs.Offset, Globals.SOCKET_IO_BUFSIZE);
                        int howManyBytes = Encoding.UTF8.GetBytes(
                                response,
                                0,
                                response.Length,
                                eArgs.Buffer,
                                eArgs.Offset
                            );
                        client.wsHandshakeComplete = true;
                        return howManyBytes;
                    }
                    else
                    {
                        // the headers packet is a GET request so it should say GET somewhere in it.
                        // if not a websocket handshake request then assume its a raw TCP client.
                        _logger.Warn($"Data from {client.id} was not formatted as a websocket handshake. Returning int.MaxValue from handshake function");
                        client.wsHandshakeComplete = true;
                        return int.MaxValue;
                    }
                }
                else
                {
                    _logger.Error("Websocket handshake was attempted but data received was < 3 bytes so couldn't proceed.");
                    return 0;
                }
            }
            else
            {
                _logger.Warn("Couldn't do websocket handshake because a value was null.");
                return 0;
            }
        }

        /// <summary>
        /// prep the SAEA buffer with the data.
        /// </summary>
        /// <param name="eArg">The SAEA instance we are modifying</param>
        /// <param name="byteCount">The number of bytes comprising the message</param>
        /// <returns>The length of the encoded message</returns>
        public static int EncodeMessage(ref SocketAsyncEventArgs eArgs, int byteCount, byte opcode)
        {
            byte payload_length_descriptor;
            byte[] payload_length_bytes = new byte[0];

            // byte[] payload length is either: >255, so can fit in byte, 2-byte int so UInt16, or 8, UInt64
            if (byteCount < 125)
            {
                payload_length_descriptor = (byte)byteCount;
            }
            else if (byteCount < ushort.MaxValue)
            {
                payload_length_descriptor = 126;
                payload_length_bytes = BitConverter.GetBytes((ushort)byteCount);
                Array.Reverse(payload_length_bytes);
            }
            else
            {
                payload_length_descriptor = 127;
                payload_length_bytes = BitConverter.GetBytes((ulong)byteCount);
                Array.Reverse(payload_length_bytes);
            }

            // frame buffer
            byte[] frame = new byte[2 + payload_length_bytes.Length + byteCount];
            int frameLength = 2 + payload_length_bytes.Length + byteCount;

            // metadata
            frame[0] = (byte)(0b10000000 | opcode); // FINal message. Add opcode.
            frame[1] = payload_length_descriptor; // add the length descriptor.

            // add the payload length bytes onto the frame metadata, the payload onto the frame, then copy the frame into the send buffer.
            Array.Copy(payload_length_bytes, 0, frame, 2, payload_length_bytes.Length);
            Array.Copy(eArgs.Buffer, eArgs.Offset, frame, 2 + payload_length_bytes.Length, byteCount);
            Array.Copy(frame, 0, eArgs.Buffer, eArgs.Offset, frame.Length);

            return frameLength;
        }

        /*  
         *  %x0 denotes a continuation frame
         *  %x1 denotes a text frame
         *  %x2 denotes a binary frame
         *  %x3-7 are reserved for further non-control frames
         *  %x8 denotes a connection close
         *  %x9 denotes a ping
         *  %xA denotes a pong
         *  %xB-F are reserved for further control frames
         */

        public enum Opcode : byte
        {
            continuation = 0b00000000,
            text = 0b00000001,
            bin = 0b00000010,
            close = 0b00001000,
            ping = 0b00001001,
            pong = 0b00001010,
            unrecognised
        }

        /// <summary>
        /// Process a client --> server message. Should handle fragmentation, haven't been able to test
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns>true if message was</returns>
        public static (int msgLen, Opcode opcode) DecodeMessage(ref SocketAsyncEventArgs eArgs)
        {
            if (eArgs.BytesTransferred > 3)
            {
                // the masked data
                byte[] bytes = eArgs.Buffer[eArgs.Offset..(eArgs.Offset + eArgs.BytesTransferred)];
                
                // length of decoded data
                int decodedLength = 0;

                // first byte - FIN (Bit 0), Reserved: (Bits 1:3), Opcode (Bit 4:7)
                bool final = (bytes[0] & 0b01111111 >> 7) == 1;
                int opcode = bytes[0] & 0b00001111;

                // second byte - Masked?: (Bit 0), Payload-Length: (Bits 1:7)
                bool masked = bytes[1] >> 7 == 1;
                ulong payload_len = (ulong)bytes[1] & 0b01111111;
                int offset = 2;

                // _logger.Debug("First byte: " + GetBits(bytes[0]));
                // _logger.Debug("Second byte: " + GetBits(bytes[1]));
                // _logger.Debug("Final: " + final);
                // _logger.Debug("Opcode: " + opcode);
                // _logger.Debug("Masked?: " + masked);
                // _logger.Debug($"WS message payload length: {payload_len}");

                if (payload_len != 0)
                {
                    // read payload length if we need to.
                    // bytes are reversed because websocket will print them in Big-Endian, whereas BitConverter will want them arranged in little-endian on windows
                    if (payload_len == 126)
                    {
                        payload_len = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
                        offset += 2;
                    }
                    else if (payload_len == 127)
                    {
                        payload_len = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..10]);
                        offset += 8;
                    }
                    if (masked)
                    {
                        // algorithm - XORing bytes[i] and mask arr
                        byte[] mask = bytes[offset..(offset + 4)];
                        byte[] decoded = bufferPool.Rent((int)payload_len);
                        offset += 4;
                        ulong uoffset = (ulong)offset;
                        for (ulong i = 0; i < payload_len; ++i)
                        {
                            // i % 4 = 1,2,3,4, 1,2,3,4, 1,2,3,4
                            decoded[i] = (byte)(bytes[uoffset + i] ^ mask[i % 4]);
                        }
                        Array.Copy(decoded, 0, eArgs.Buffer, eArgs.Offset, decoded.Length);
                        decodedLength = decoded.Length;
                        bufferPool.Return(decoded);
                        if (Enum.IsDefined(typeof(Opcode), (byte)opcode))
                        {
                            return (decodedLength, (Opcode)opcode);
                        }
                        return (decodedLength, Opcode.unrecognised);
                    }
                    else
                    {
                        // client --> server messages should always be masked, this shouldn't happen.
                        // close the connection. "The server MUST close the connection upon receiving a frame that is not masked" https://datatracker.ietf.org/doc/html/rfc6455#section-5.1
                        return (0, Opcode.close);
                    }
                }
            }
            return (0, Opcode.close);
        }

        //        bool final = false;
        //int opcode = int.MaxValue;
        //List<byte[]> decoded_frame_payloads = new List<byte[]>();

        //while (!final && opcode != 0)
        //{
        //    // wait for data
        //    while (_clientSocket.Available < 3) ;
        //    byte[] bytes = new byte[_clientSocket.Available];
        //    await _clientSocket.ReceiveAsync(bytes, SocketFlags.None);

        //    // first byte - FIN (Bit 0), Reserved: (Bits 1:3), Opcode (Bit 4:7)
        //    final = (bytes[0] & 0b01111111 >> 6) == 1;
        //    opcode = bytes[0] & 0b00001111;

        //    // second byte - Masked?: (Bit 0), Payload-Length: (Bits 1:7)
        //    bool masked = (bytes[1] >> 7) == 1;
        //    ulong payload_len = (ulong)bytes[1] & 0b01111111;
        //    int offset = 2;

        //    if (payload_len != 0)
        //    {
        //        // read payload length if we need to.
        //        // bytes are reversed because websocket will print them in Big-Endian, whereas BitConverter will want them arranged in little-endian on windows
        //        if (payload_len == 126)
        //        {
        //            payload_len = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
        //            offset += 2;
        //        }
        //        else if (payload_len == 127)
        //        {
        //            payload_len = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..10]);
        //            offset += 8;
        //        }
        //        if (masked)
        //        {
        //            // algorithm - XORing bytes[i] and mask arr
        //            byte[] mask = bytes[offset..(offset + 4)];
        //            byte[] decoded = new byte[payload_len];
        //            offset += 4;
        //            ulong uoffset = (ulong)offset;
        //            for (ulong i = 0; i < payload_len; ++i)
        //            {
        //                // i % 4 = 1,2,3,4, 1,2,3,4, 1,2,3,4
        //                decoded[i] = (byte)(bytes[uoffset + i] ^ mask[i % 4]);
        //            }
        //            decoded_frame_payloads.Add(decoded);
        //        }
        //        else
        //        {
        //            // client --> server messages should always be masked, this shouldn't happen.
        //            // close the connection. "The server MUST close the connection upon receiving a frame that is not masked" https://datatracker.ietf.org/doc/html/rfc6455#section-5.1
        //            _clientSocket.Dispose();
        //            throw new InvalidDataException("Data received from websocket was not masked, closing connection.");
        //        }
        //    }
        //    else
        //    {
        //        Console.WriteLine("stated length of websocket frame was 0");
        //        decoded_frame_payloads.Add(bytes[offset..]);
        //    }
        //}
        //return decoded_frame_payloads.SelectMany(frame_data => frame_data).ToArray();

        ///// <summary>
        ///// Send a fragmented message server --> client. One frame for each yielded byte array. 
        ///// After break from iterable send empty frame indicating finish.
        ///// </summary>
        ///// <param name="iterable"></param>
        ///// <param name="opcode"></param>
        ///// <returns></returns>
        //public static void EncodeAndSendFragmentedMessage(IAsyncEnumerable<byte[]> iterable, byte opcode)
        //{
        //    byte firstByte = opcode;
        //    await foreach (byte[] bytes in iterable)
        //    {
        //        byte payload_length_descriptor;
        //        byte[] payload_length_bytes = new byte[0];

        //        // byte[] payload length is either: >255, so can fit in byte, 2-byte int so UInt16, or 8, UInt64
        //        if (bytes.Length < 125)
        //        {
        //            payload_length_descriptor = (byte)bytes.Length;
        //        }
        //        else if (bytes.Length < ushort.MaxValue)
        //        {
        //            payload_length_descriptor = 126;
        //            payload_length_bytes = BitConverter.GetBytes((ushort)bytes.Length);
        //        }
        //        else
        //        {
        //            payload_length_descriptor = 127;
        //            payload_length_bytes = BitConverter.GetBytes((ulong)bytes.Length);
        //        }

        //        // websocket frame
        //        byte[] frame = new byte[2 + payload_length_bytes.Length + bytes.Length];

        //        // metadata
        //        frame[0] = firstByte;
        //        frame[1] = payload_length_descriptor; // add the length descriptor.
        //        firstByte = (byte)Opcodes.continuation; // == 0

        //        // add the payload length bytes onto the metadata on the frame, and then the payload onto the frame
        //        Array.Copy(payload_length_bytes, 0, frame, 2, payload_length_bytes.Length);
        //        Array.Copy(bytes, 0, frame, 2 + payload_length_bytes.Length, bytes.Length);

        //        // send the bytes!
        //        TcpUtils.SendBytes(_clientSocket, frame);
        //    }
        //    // send an empty frame to signify end of message
        //    TcpUtils.SendBytes(
        //        _clientSocket,
        //        new byte[]{
        //            0b10000000,
        //            0b00000000
        //        }
        //    );
        //}
    }
}
//---------------------------------------------------------------------------------
// Copyright (c) September 2021, devMobile Software
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
//---------------------------------------------------------------------------------
namespace devMobile.IoT.LoRaWan
{
   using System;
   using System.Diagnostics;
   using System.IO.Ports;
   using System.Text;
   using System.Threading;

   public enum LoRaClass
   {
      Undefined = 0,
      A,
      B,
      C
   }

   public enum Result
   {
      Undefined = 0,
      Success,
      ATCommandResponseTimeout,
      JoinFailed,
      ErrorIsInvalidFormat,
      CommandResponseTimeout,
      CommandIsUnknown,
      ParameterIsInvalid,
      CommandIsInWrongFormat,
      CommandIsUnavilableInCurrentMode,
      TooManyParameters,
      CommandIsTooLong,
      ReceiveEndSymbolTimeout,
      InvalidCharacterReceived,
      CommandError
   }

   public class SeeedE5LoRaWANDevice : IDisposable
   {
      public const ushort BaudRateMinimum = 1200;
      public const ushort BaudRateMaximum = 57600;
      public const ushort RegionIDLength = 5;
      public const ushort AppEuiLength = 23;
      public const ushort AppKeyLength = 32;
      public const ushort DevAddrLength = 11;
      public const ushort NwsKeyLength = 32;
      public const ushort AppsKeyLength = 32;
      public const ushort MessagePortMinimumValue = 1;
      public const ushort MessagePortMaximumValue = 223;
      public const ushort MessageBytesMaximumLength = 242;
      public const ushort MessageBcdMaximumLength = 484;

      public readonly TimeSpan SendTimeoutMinimum = new TimeSpan(0, 0, 1);
      public readonly TimeSpan SendTimeoutMaximum = new TimeSpan(0, 0, 30);

      public readonly TimeSpan JoinTimeoutMinimum = new TimeSpan(0, 0, 1);
      public readonly TimeSpan JoinTimeoutMaximum = new TimeSpan(0, 0, 30);

      private const string ErrorMarker = "ERROR";
      private const string JoinFailedMarker = "Join failed";
      private const string DownlinkPayloadMarker = "+MSGHEX: PORT: ";
      private const string DownlinkMetricsMarker = "+MSGHEX: RXWIN";
      private const string DownlinkConfirmedPayloadMarker = "+CMSGHEX: PORT: ";
      private const string DownlinkConfirmedMetricsMarker = "+CMSGHEX: RXWIN";
      private readonly TimeSpan CommandTimeoutDefault = new TimeSpan(0, 0, 5);

      private SerialPort serialDevice = null;

      private string atCommandExpectedResponse;
      private readonly AutoResetEvent atExpectedEvent;
      private StringBuilder response;
      private Result result;
      private byte DownlinkPort = 0;
      private string DownlinkPayload = null;

      public delegate void MessageConfirmationHandler(int rssi, double snr);
      public MessageConfirmationHandler OnMessageConfirmation;
      public delegate void ReceiveMessageHandler(int port, int rssi, double snr, string payload);
      public ReceiveMessageHandler OnReceiveMessage;

      public SeeedE5LoRaWANDevice()
      {
         response = new StringBuilder(128);
         this.atExpectedEvent = new AutoResetEvent(false);
      }

      public Result Initialise(string serialPortId, int baudRate, Parity serialParity = Parity.None, ushort dataBits = 8, StopBits stopBitCount = StopBits.One)
      {
         if ((serialPortId == null) || (serialPortId == ""))
         {
            throw new ArgumentException("Invalid SerialPortId", "serialPortId");
         }
         if ((baudRate < BaudRateMinimum) || (baudRate > BaudRateMaximum))
         {
            throw new ArgumentException("Invalid BaudRate", "baudRate");
         }

         serialDevice = new SerialPort(serialPortId);

         // set parameters
         serialDevice.BaudRate = baudRate;
         serialDevice.Parity = serialParity;
         serialDevice.StopBits = stopBitCount;
         serialDevice.Handshake = Handshake.None;
         serialDevice.DataBits = dataBits;
         serialDevice.NewLine = "\r\n";

         atCommandExpectedResponse = string.Empty;

         serialDevice.Open();

         serialDevice.DataReceived += SerialDevice_DataReceived;

         // Ignoring the return from this is intentional
         this.SendCommand("+LOWPOWER: WAKEUP", "AT+LOWPOWER: WAKEUP", SendTimeoutMinimum);

         return Result.Success;
      }

      public Result Class(LoRaClass loRaClass)
      {
         string command;
         string response;

         switch (loRaClass)
         {
            case LoRaClass.A:
               command = "AT+CLASS=A";
               response = "+CLASS: A";
               break;
            case LoRaClass.B:
               command = "AT+CLASS=B";
               response = "+CLASS: B";
               break;
            case LoRaClass.C:
               command = "AT+CLASS=C";
               response = "+CLASS: C";
               break;
            default:
               throw new ArgumentException($"LoRa class value {loRaClass} invalid", "loRaClass");
         }

         // Set the class
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+CLASS:{loRaClass}");
#endif
         Result result = SendCommand(response, command, CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+CLASS failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Port(byte port)
      {
         if ((port < MessagePortMinimumValue) || (port > MessagePortMaximumValue))
         {
            throw new ArgumentException($"port invalid must be greater than or equal to {MessagePortMinimumValue} and less than or equal to {MessagePortMaximumValue}", "port");
         }

         // Set the port number
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+PORT:{port}");
#endif
         Result result = SendCommand($"+PORT: {port}", $"AT+PORT={port}", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+PORT failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Region(string regionID)
      {
         if (regionID.Length != RegionIDLength)
         {
            throw new ArgumentException($"RegionID {regionID} length {regionID.Length} invalid", "regionID");
         }

#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+DR={regionID}");
#endif
         Result result = SendCommand($"+DR: {regionID}", $"AT+DR={regionID}", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+DR= failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Reset()
      {
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+RESET");
#endif
         Result result = SendCommand($"+RESET: OK", $"AT+RESET", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+RESET failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Sleep()
      {
         // Put the E5 module to sleep
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+LOWPOWER");
#endif
         Result result = SendCommand("+LOWPOWER: SLEEP", $"AT+LOWPOWER", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} device:sleep failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Wakeup()
      {
         // Wakeup the E5 Module
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+LOWPOWER: WAKEUP");
#endif
         Result result = SendCommand("+LOWPOWER: WAKEUP", $"A", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+LOWPOWER: WAKEUP failed {result}");
#endif
            return result;
         }

         // Thanks AndrewL for pointing out delay required in section 4.30 LOWPOWER
         Thread.Sleep(10);

         return Result.Success;
      }

      public Result AdrOff()
      {
         // Adaptive Data Rate off
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +ADR=OFF");
#endif
         Result result = SendCommand("+ADR: OFF", "AT+ADR=OFF", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +ADR=OFF failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result AdrOn()
      {
         // Adaptive Data Rate on
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +ADR=ON");
#endif
         Result result = SendCommand("+ADR: ON", "AT+ADR=ON", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +ADR=ON failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result AbpInitialise(string devAddr, string nwksKey, string appsKey)
      {
         Result result;

         if ((devAddr == null) || (devAddr.Length != DevAddrLength))
         {
            throw new ArgumentException($"devAddr invalid length must be {DevAddrLength} characters", "devAddr");
         }
         if ((nwksKey == null) || (nwksKey.Length != NwsKeyLength))
         {
            throw new ArgumentException($"nwsKey invalid length must be {NwsKeyLength} characters", "nwsKey");
         }
         if ((appsKey == null) || (appsKey.Length != AppsKeyLength))
         {
            throw new ArgumentException($"appsKey invalid length must be {AppsKeyLength} characters", "appsKey");
         }

         // Set the Working mode to LoRaWAN
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+MODE=LWABP");
#endif
         // Set the Mode to ABP
         result = SendCommand($"+MODE: LWABP", "AT+MODE=LWABP", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+MODE=LWABP failed {result}");
#endif
            return result;
         }

         // set the devAddr
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+ID=DEVADDR,\"{devAddr}\"");
#endif
         StringBuilder devAddrWithSpaces = new StringBuilder(devAddr);

         result = SendCommand($"+ID: DevAddr, {devAddr}", $"AT+ID=DEVADDR,\"{devAddrWithSpaces.Replace(':', ' ')}\"", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+ID=DEVADDR failed {result}");
#endif
            return result;
         }

         // Set the nwsKey
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=NWKSKEY:\"{nwksKey}\"");
#endif
         result = SendCommand($"+KEY=NWKSKEY:\"{nwksKey}\"", $"AT+KEY=NWKSKEY:\"{nwksKey}\"", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=NWKSKEY failed {result}");
#endif
            return result;
         }

         // Set the appsKey
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=APPSKEY:\"{appsKey}\"");
#endif
         result = SendCommand($"+KEY=APPSKEY:\"{appsKey}\"", $"AT+KEY=APPSKEY:\"{appsKey}\"", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=APPSKEY failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result OtaaInitialise(string appEui, string appKey)
      {
         Result result;

         if ((appEui == null) || (appEui.Length != AppEuiLength))
         {
            throw new ArgumentException($"appEui invalid length must be {AppEuiLength} characters", "appEui");
         }
         if ((appKey == null) || (appKey.Length != AppKeyLength))
         {
            throw new ArgumentException($"appKey invalid length must be {AppKeyLength} characters", "appKey");
         }

         // Set the Mode to OTAA
         result = SendCommand($"+MODE: LWOTAA", "AT+MODE=LWOTAA", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+MODE=LWOTAA failed {result}");
#endif
            return result;
         }

         // Set the appEUI
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+ID=APPEUI:{appEui}");
#endif
         StringBuilder appEuiWithSpaces = new StringBuilder(appEui);

         result = SendCommand($"+ID: AppEui, {appEui}", $"AT+ID=APPEUI,\"{appEuiWithSpaces.Replace(':', ' ')}\"", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+ID=AppEui failed {result}");
#endif
            return result;
         }

         // Set the appKey
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=APPKEY:{appKey}");
#endif
         result = SendCommand($"+KEY: APPKEY {appKey}", $"AT+KEY=APPKEY,{appKey}", CommandTimeoutDefault);
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=APPKEY failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Join(bool force, TimeSpan timeout)
      {
         Result result;

         if ((timeout < JoinTimeoutMinimum) || (timeout > JoinTimeoutMaximum))
         {
            throw new ArgumentException($"timeout invalid must be greater than or equal to  {JoinTimeoutMinimum.TotalSeconds} seconds and less than or equal to {JoinTimeoutMaximum.TotalSeconds} seconds", "timeout");
         }

         // Join the network
         if (force)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+JOIN=FORCE");
#endif
            result = SendCommand("+JOIN: Done", $"AT+JOIN=FORCE", timeout);
         }
         else
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+JOIN");
#endif
            result = SendCommand("+JOIN: Done", $"AT+JOIN", timeout);
         }

         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+JOIN failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Send(string payload, bool confirmed, TimeSpan timeout)
      {
         Result result;

         if ((payload == null) || (payload.Length > MessageBcdMaximumLength))
         {
            throw new ArgumentException($"payload invalid length must be less than or equal to {MessageBcdMaximumLength} BCD characters long", "payload");
         }

         if ((timeout < SendTimeoutMinimum) || (timeout > SendTimeoutMaximum))
         {
            throw new ArgumentException($"timeout invalid must be greater than or equal to  {SendTimeoutMinimum.TotalSeconds} seconds and less than or equal to {SendTimeoutMaximum.TotalSeconds} seconds", "timeout");
         }

         // Send message the network
         if (confirmed)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +CMSGHEX payload {payload} timeout {timeout.TotalSeconds} seconds");
#endif
            if (payload.Length > 0)
            {
               result = SendCommand("+CMSGHEX: Done", $"AT+CMSGHEX=\"{payload}\"", timeout);
            }
            else
            {
               result = SendCommand("+CMSGHEX: Done", $"AT+CMSGHEX", timeout);
            }
         }
         else
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +MSGHEX payload {payload} timeout {timeout.TotalSeconds} seconds");
#endif
            if (payload.Length > 0)
            {
               result = SendCommand("+MSGHEX: Done", $"AT+MSGHEX=\"{payload}\"", timeout);
            }
            else
            {
               result = SendCommand("+MSGHEX: Done", $"AT+MSGHEX", timeout);
            }
         }
         if (result != Result.Success)
         {
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+MSGHEX failed {result}");
#endif
            return result;
         }

         return Result.Success;
      }

      public Result Send(byte[] payloadBytes, bool confirmed, TimeSpan timeout)
      {
         if ((payloadBytes == null) || (payloadBytes.Length > MessageBytesMaximumLength))
         {
            throw new ArgumentException($"payload invalid length must be less than or equal to {MessageBytesMaximumLength} bytes long", "payloadBytes");
         }

         if ((timeout < SendTimeoutMinimum) || (timeout > SendTimeoutMaximum))
         {
            throw new ArgumentException($"timeout invalid must be greater than or equal to  {SendTimeoutMinimum.TotalSeconds} seconds and less than or equal to {SendTimeoutMaximum.TotalSeconds} seconds", "timeout");
         }

         // Send message the network
#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} Send payload {BytesToBcd(payloadBytes)} timeout {timeout.TotalSeconds} seconds");
#endif
         return Send(BytesToBcd(payloadBytes), confirmed, timeout);
      }

      private Result SendCommand(string expectedResponse, string command, TimeSpan timeout)
      {
         if (string.IsNullOrEmpty(expectedResponse))
         {
            throw new ArgumentException($"expectedResponse invalid length cannot be empty", "expectedResponse");
         }
         if (string.IsNullOrEmpty(command))
         {
            throw new ArgumentException($"command invalid length cannot be empty", "command");
         }

         this.atCommandExpectedResponse = expectedResponse;

         serialDevice.WriteLine(command);

         this.atExpectedEvent.Reset();

         if (!this.atExpectedEvent.WaitOne((int)timeout.TotalMilliseconds, false))
            return Result.ATCommandResponseTimeout;

         this.atCommandExpectedResponse = string.Empty;

         return result;
      }

      private Result ModemErrorParser(string errorText)
      {
         Result result = Result.Undefined;

         switch (errorText)
         {
            case "(-1)":
               result = Result.ParameterIsInvalid;
               break;
            case "(-10)":
               result = Result.CommandIsUnknown;
               break;
            case "(-11)":
               result = Result.CommandIsInWrongFormat;
               break;
            case "(-12)":
               result = Result.CommandIsUnavilableInCurrentMode;
               break;
            case "(-20)":
               result = Result.TooManyParameters;
               break;
            case "(-21)":
               result = Result.CommandIsTooLong;
               break;
            case "(-22)":
               result = Result.ReceiveEndSymbolTimeout;
               break;
            case "(-23)":
               result = Result.InvalidCharacterReceived;
               break;
            case "(-24)":
               result = Result.CommandError;
               break;
            default:
               result = Result.ErrorIsInvalidFormat;
               break;
         }

         return result;
      }

      private void SerialDevice_DataReceived(object sender, SerialDataReceivedEventArgs e)
      {
         SerialPort serialDevice = (SerialPort)sender;

         response.Append(serialDevice.ReadExisting());

         int eolPosition;
         do
         {
            // extract a line
            eolPosition = response.ToString().IndexOf(serialDevice.NewLine);

            if (eolPosition != -1)
            {
               string line = response.ToString(0, eolPosition);
               response = response.Remove(0, eolPosition + serialDevice.NewLine.Length);
#if DIAGNOSTICS
               Debug.WriteLine($" Line :{line} ResponseExpected:{atCommandExpectedResponse} Response:{response}");
#endif
               int joinFailIndex = line.IndexOf(JoinFailedMarker);
               if (joinFailIndex != -1)
               {
                  result = Result.JoinFailed;
                  atExpectedEvent.Set();
               }

               int errorIndex = line.IndexOf(ErrorMarker);
               if (errorIndex != -1)
               {
                  string errorNumber = line.Substring(errorIndex + ErrorMarker.Length);

                  result = ModemErrorParser(errorNumber.Trim());
                  atExpectedEvent.Set();
               }

               if (atCommandExpectedResponse != string.Empty)
               {
                  int successIndex = line.IndexOf(atCommandExpectedResponse);
                  if (successIndex != -1)
                  {
                     result = Result.Success;
                     atExpectedEvent.Set();
                  }
               }

               // If a downlink message payload then stored ready for metrics
               if ((line.IndexOf(DownlinkPayloadMarker) != -1) || (line.IndexOf(DownlinkConfirmedPayloadMarker) != -1))
               {
                  string receivedMessageLine = line.Substring(DownlinkPayloadMarker.Length);

                  string[] fields = receivedMessageLine.Split(':', ';');
                  DownlinkPort = byte.Parse(fields[0]);
                  DownlinkPayload = fields[2].Trim(' ', '"');
               }

               if ((line.IndexOf(DownlinkMetricsMarker) != -1) || (line.IndexOf(DownlinkConfirmedMetricsMarker) != -1))
               {
                  string receivedMessageLine = line.Substring(DownlinkMetricsMarker.Length);

                  string[] fields = receivedMessageLine.Split(' ', ',');
                  int rssi = int.Parse(fields[3]);
                  double snr = double.Parse(fields[6]);

                  if (DownlinkPort != 0)
                  {
                     if (this.OnReceiveMessage != null)
                     {
                        OnReceiveMessage(DownlinkPort, rssi, snr, DownlinkPayload);
                     }
                     DownlinkPort = 0;
                  }

                  if (line.IndexOf(DownlinkConfirmedMetricsMarker) != -1)
                  {
                     if (this.OnMessageConfirmation != null)
                     {
                        OnMessageConfirmation(rssi, snr);
                     }
                  }
               }
            }
         }
         while (eolPosition != -1);
      }

      // Utility functions for clients for processing messages payloads to be send, ands messages payloads received.

      public static string BytesToBcd(byte[] payloadBytes)
      {
         Debug.Assert(payloadBytes != null);

         StringBuilder payloadBcd = new StringBuilder(BitConverter.ToString(payloadBytes));

         payloadBcd = payloadBcd.Replace("-", "");

         return payloadBcd.ToString();
      }

      public static byte[] BcdToByes(string payloadBcd)
      {
         Debug.Assert(payloadBcd != null);
         Debug.Assert(payloadBcd != String.Empty);
         Debug.Assert(payloadBcd.Length % 2 == 0);
         Byte[] payloadBytes = new byte[payloadBcd.Length / 2];
         string digits = "0123456789ABCDEF";

         char[] chars = payloadBcd.ToUpper().ToCharArray();

         for (int index = 0; index < payloadBytes.Length; index++)
         {
            byte byteHigh = (byte)digits.IndexOf(chars[index * 2]);
            byte byteLow = (byte)digits.IndexOf(chars[(index * 2) + 1]);

            payloadBytes[index] += (byte)(byteHigh * 16);
            payloadBytes[index] += byteLow;
         }

         return payloadBytes;
      }

      public void Dispose()
      {
         if (serialDevice != null)
         {
            serialDevice.Dispose();
            serialDevice = null;
         }
      }
   }
}
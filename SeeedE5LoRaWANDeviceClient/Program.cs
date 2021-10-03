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
// Must have one of following options defined in the nfproj file
//    PAYLOAD_BCD or PAYLOAD_BYTES
//    OTAA or ABP
//
// Optional definitions
//    CONFIRMED For confirmed messages
//    RESET for retun device to factory settings
//
//---------------------------------------------------------------------------------
namespace devMobile.IoT.LoRaWAN.NetCore.SeeedLoRaE5
{
	using System;
	using System.IO.Ports;
	using System.Threading;


   public class Program
   {
      private const string SerialPortId = "/dev/ttyS0";
      private const LoRaClass Class = LoRaClass.A;
      private const string Region = "AS923";
      private const byte MessagePort = 15;
      private static readonly TimeSpan MessageSendTimerDue = new TimeSpan(0, 0, 15);
      private static readonly TimeSpan MessageSendTimerPeriod = new TimeSpan(0, 5, 0);
      private static Timer MessageSendTimer;
#if PAYLOAD_BCD
      private const string PayloadBcd = "010203040506070809";
#endif
#if PAYLOAD_BYTES
      private static readonly byte[] PayloadBytes = { 0x09, 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 };
#endif

      public static void Main()
      {
         Result result;

         Console.WriteLine("devMobile.IoT.LoRaWAN.NetCore.SeeedLoRaE5 DeviceClient starting");

         Console.WriteLine($"Serial ports:{String.Join(",", SerialPort.GetPortNames())}");

         try
         {
            using (SeeedE5LoRaWANDevice device = new SeeedE5LoRaWANDevice())
            {
               result = device.Initialise(SerialPortId, 9600, Parity.None, 8, StopBits.One);
               if (result != Result.Success)
               {
                  Console.WriteLine($"Initialise failed {result}");
                  return;
               }

               MessageSendTimer = new Timer(SendMessageTimerCallback, device, Timeout.Infinite, Timeout.Infinite);

               device.OnJoinCompletion += OnJoinCompletionHandler;
               device.OnReceiveMessage += OnReceiveMessageHandler;
#if CONFIRMED
					device.OnMessageConfirmation += OnMessageConfirmationHandler;
#endif

               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Class {Class}");
               result = device.Class(Class);
               if (result != Result.Success)
               {
                  Console.WriteLine($"Class failed {result}");
                  return;
               }

#if RESET
               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Reset");
               result = device.Reset();
               if (result != Result.Success)
               {
                  Console.WriteLine($"Reset failed {result}");
                  return;
               }
#endif

               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Region {Region}");
               result = device.Region(Region);
               if (result != Result.Success)
               {
                  Console.WriteLine($"Region failed {result}");
                  return;
               }

               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} ADR On");
               result = device.AdrOn();
               if (result != Result.Success)
               {
                  Console.WriteLine($"ADR on failed {result}");
                  return;
               }

               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Port {MessagePort}");
               result = device.Port(MessagePort);
               if (result != Result.Success)
               {
                  Console.WriteLine($"Port on failed {result}");
                  return;
               }

#if OTAA
               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} OTAA");
               result = device.OtaaInitialise(Config.AppEui, Config.AppKey);
               if (result != Result.Success)
               {
                  Console.WriteLine($"OTAA Initialise failed {result}");
                  return;
               }
#endif

#if ABP
               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} ABP");
               result = device.AbpInitialise(Config.DevAddress, Config.NwksKey, Config.AppsKey);
               if (result != Result.Success)
               {
                  Console.WriteLine($"ABP Initialise failed {result}");
                  return;
               }
#endif

               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Join start");
               result = device.Join(true);
               if (result != Result.Success)
               {
                  Console.WriteLine($"Join start failed {result}");
                  return;
               }
               Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Join started");

               Thread.Sleep(Timeout.Infinite);
            }
         }
         catch (Exception ex)
         {
            Console.WriteLine(ex.Message);
         }
      }

      private static void OnJoinCompletionHandler(bool result)
      {
         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Join finished:{result}");

         if (result)
         {
            MessageSendTimer.Change(MessageSendTimerDue, MessageSendTimerPeriod);
         }
      }

      private static void SendMessageTimerCallback(object state)
      {
         Result result;
         SeeedE5LoRaWANDevice device = (SeeedE5LoRaWANDevice)state;
#if CONFIRMED
         Boolean Confirmed = true;
#else
         Boolean Confirmed = false;
#endif

#if LOW_POWER
         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Wakeup");
         result = device.Wakeup();
         if (result != Result.Success)
         {
            Console.WriteLine($"Wakeup failed {result}");
            return;
         }
#endif

#if PAYLOAD_BCD
         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Send payload BCD:{PayloadBcd}");
         result = device.Send(PayloadBcd, Confirmed);
#endif

#if PAYLOAD_BYTES
         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Send payload Bytes:{BitConverter.ToString(PayloadBytes)}");
         result = device.Send(PayloadBytes, Confirmed);
#endif
         if (result != Result.Success)
         {
            Console.WriteLine($"Send failed {result}");
         }

#if LOW_POWER
         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Sleep");
         result = device.Sleep();
         if (result != Result.Success)
         {
            Console.WriteLine($"Sleep failed {result}");
            return;
         }
#endif
      }

#if CONFIRMED
      static void OnMessageConfirmationHandler(int rssi, double snr)
      {
         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Send Confirm RSSI:{rssi} SNR:{snr}");
      }
#endif

      static void OnReceiveMessageHandler(int port, int rssi, double snr, string payloadBcd)
      {
         byte[] payloadBytes = SeeedE5LoRaWANDevice.BcdToByes(payloadBcd);

         Console.WriteLine($"{DateTime.UtcNow:hh:mm:ss} Receive Message RSSI:{rssi} SNR:{snr} Port:{port} Payload:{payloadBcd} PayLoadBytes:{BitConverter.ToString(payloadBytes)}");
      }
   }
}

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
namespace devMobile.IoT.LoRaWAN.NetCore.SeeedLoRaE5
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
		ModemIsBusy,
		NetworkNotJoined,
		NetworkAlreadyJoined,
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

		private SerialPort serialDevice = null;
		private const int CommandTimeoutDefaultmSec = 1500;
		private Thread CommandResponsesProcessorThread = null;
		private Boolean CommandProcessResponses = true;
		private string CommandExpectedResponse;
		private readonly AutoResetEvent atExpectedEvent;
		private Result result;

		public delegate void JoinCompletionHandler(bool joinSuccessful);
		public JoinCompletionHandler OnJoinCompletion;
		public delegate void MessageConfirmationHandler(int rssi, double snr);
		public MessageConfirmationHandler OnMessageConfirmation;
		public delegate void ReceiveMessageHandler(int port, int rssi, double snr, string payload);
		public ReceiveMessageHandler OnReceiveMessage;

		public SeeedE5LoRaWANDevice()
		{
			this.atExpectedEvent = new AutoResetEvent(false);
		}

		public Result Initialise(string serialPortId, int baudRate, Parity serialParity = Parity.None, ushort dataBits = 8, StopBits stopBitCount = StopBits.One)
		{
			if ((serialPortId == null) || (serialPortId == ""))
			{
				throw new ArgumentException("Invalid SerialPortId", nameof(serialPortId));
			}
			if ((baudRate < BaudRateMinimum) || (baudRate > BaudRateMaximum))
			{
				throw new ArgumentException("Invalid BaudRate", nameof(baudRate));
			}

			serialDevice = new SerialPort(serialPortId);

			// set parameters
			serialDevice.BaudRate = baudRate;
			serialDevice.Parity = serialParity;
			serialDevice.StopBits = stopBitCount;
			serialDevice.Handshake = Handshake.None;
			serialDevice.DataBits = dataBits;
			serialDevice.NewLine = "\r\n";

			serialDevice.ReadTimeout = CommandTimeoutDefaultmSec;

			CommandExpectedResponse = string.Empty;

			serialDevice.Open();
			// clear out the input buffer.
			serialDevice.ReadExisting();

			// Only start up the serial port polling thread if the port opened successfuly
			CommandResponsesProcessorThread = new Thread(SerialPortProcessor);
			CommandResponsesProcessorThread.Start();

			// Ignoring the return from this is intentional
			this.SendCommand("+LOWPOWER: WAKEUP", "AT+LOWPOWER: WAKEUP");

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
					throw new ArgumentException($"LoRa class value {loRaClass} invalid", nameof(loRaClass));
			}

			// Set the class
#if DIAGNOSTICS
			Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+CLASS:{loRaClass}");
#endif
			Result result = SendCommand(response, command);
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
				throw new ArgumentException($"port invalid must be greater than or equal to {MessagePortMinimumValue} and less than or equal to {MessagePortMaximumValue}", nameof(port));
			}

			// Set the port number
#if DIAGNOSTICS
			Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+PORT:{port}");
#endif
			Result result = SendCommand($"+PORT: {port}", $"AT+PORT={port}");
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
				throw new ArgumentException($"RegionID {regionID} length {regionID.Length} invalid", nameof(regionID));
			}

#if DIAGNOSTICS
			Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+DR={regionID}");
#endif
			Result result = SendCommand($"+DR: {regionID}", $"AT+DR={regionID}");
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
			Result result = SendCommand($"+RESET: OK", $"AT+RESET");
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
			Result result = SendCommand("+LOWPOWER: SLEEP", $"AT+LOWPOWER");
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
			Result result = SendCommand("+LOWPOWER: WAKEUP", $"A");
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
			Result result = SendCommand("+ADR: OFF", "AT+ADR=OFF");
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
			Result result = SendCommand("+ADR: ON", "AT+ADR=ON");
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
				throw new ArgumentException($"devAddr invalid length must be {DevAddrLength} characters", nameof(devAddr));
			}
			if ((nwksKey == null) || (nwksKey.Length != NwsKeyLength))
			{
				throw new ArgumentException($"nwsKey invalid length must be {NwsKeyLength} characters", nameof(nwksKey));
			}
			if ((appsKey == null) || (appsKey.Length != AppsKeyLength))
			{
				throw new ArgumentException($"appsKey invalid length must be {AppsKeyLength} characters", nameof(appsKey));
			}

			// Set the Working mode to LoRaWAN
#if DIAGNOSTICS
			Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+MODE=LWABP");
#endif
			// Set the Mode to ABP
			result = SendCommand($"+MODE: LWABP", "AT+MODE=LWABP");
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

			result = SendCommand($"+ID: DevAddr, {devAddr}", $"AT+ID=DEVADDR,\"{devAddrWithSpaces.Replace(':', ' ')}\"");
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
			result = SendCommand($"+KEY=NWKSKEY:\"{nwksKey}\"", $"AT+KEY=NWKSKEY:\"{nwksKey}\"");
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
			result = SendCommand($"+KEY=APPSKEY:\"{appsKey}\"", $"AT+KEY=APPSKEY:\"{appsKey}\"");
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
				throw new ArgumentException($"appEui invalid length must be {AppEuiLength} characters", nameof(appEui));
			}
			if ((appKey == null) || (appKey.Length != AppKeyLength))
			{
				throw new ArgumentException($"appKey invalid length must be {AppKeyLength} characters", nameof(appKey));
			}

			// Set the Mode to OTAA
			result = SendCommand($"+MODE: LWOTAA", "AT+MODE=LWOTAA");
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

			result = SendCommand($"+ID: AppEui, {appEui}", $"AT+ID=APPEUI,\"{appEuiWithSpaces.Replace(':', ' ')}\"");
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
			result = SendCommand($"+KEY: APPKEY {appKey}", $"AT+KEY=APPKEY,{appKey}");
			if (result != Result.Success)
			{
#if DIAGNOSTICS
				Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+KEY=APPKEY failed {result}");
#endif
				return result;
			}

			return Result.Success;
		}

		public Result Join(bool force)
		{
			Result result;

			// Join the network
			if (force)
			{
#if DIAGNOSTICS
				Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+JOIN=FORCE");
#endif
				result = SendCommand("+JOIN: Start", $"AT+JOIN=FORCE");
			}
			else
			{
#if DIAGNOSTICS
				Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+JOIN");
#endif
				result = SendCommand("+JOIN: Start", $"AT+JOIN");
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

		public Result Send(string payload, bool confirmed)
		{
			if (payload == null)
			{
				throw new ArgumentNullException(nameof(payload));
			}

			if ((payload.Length % 2) != 0)
			{
				throw new ArgumentException("Payload length invalid must be a multiple of 2", nameof(payload));
			}

			// Send message the network
			if (confirmed)
			{
#if DIAGNOSTICS
				Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +CMSGHEX payload {payload}");
#endif
				if (payload.Length > 0)
				{
					result = SendCommand("+CMSGHEX: Start", $"AT+CMSGHEX=\"{payload}\"");
				}
				else
				{
					result = SendCommand("+CMSGHEX: Start", $"AT+CMSGHEX");
				}

				if (result != Result.Success)
				{
#if DIAGNOSTICS
					Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+CMSGHEX failed {result}");
#endif
					return result;
				}
			}
			else
			{
#if DIAGNOSTICS
				Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} +MSGHEX payload {payload}");
#endif
				if (payload.Length > 0)
				{
					result = SendCommand("+MSGHEX: Start", $"AT+MSGHEX=\"{payload}\"");
				}
				else
				{
					result = SendCommand("+MSGHEX: Start", $"AT+MSGHEX");
				}

				if (result != Result.Success)
				{
#if DIAGNOSTICS
					Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+MSGHEX failed {result}");
#endif
					return result;
				}
			}

			return Result.Success;
		}

		public Result Send(byte[] payloadBytes, bool confirmed)
		{
			if (payloadBytes == null)
			{
				throw new ArgumentNullException(nameof(payloadBytes));
			}

			// Send message the network
#if DIAGNOSTICS
			Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} Send payload {BytesToBcd(payload)}");
#endif
			return Send(BytesToBcd(payloadBytes), confirmed);
		}

		private Result SendCommand(string expectedResponse, string command)
		{
			if (expectedResponse == null)
			{
				throw new ArgumentNullException(nameof(expectedResponse));
			}

			if (expectedResponse == string.Empty)
			{
				throw new ArgumentException($"expected response invalid cannot be empty", nameof(command));
			}

			if (command == null)
			{
				throw new ArgumentNullException(nameof(command));
			}

			if (command == string.Empty)
			{
				throw new ArgumentException($"command invalid cannot be empty", nameof(command));
			}

			this.CommandExpectedResponse = expectedResponse;

			serialDevice.WriteLine(command);

			this.atExpectedEvent.Reset();

			if (!this.atExpectedEvent.WaitOne(CommandTimeoutDefaultmSec, false))
				return Result.ATCommandResponseTimeout;

			this.CommandExpectedResponse = string.Empty;

			return result;
		}


		private void SerialPortProcessor()
		{
			string line;
			int rssi;
			double snr;
			int port = 0;
			string payload = string.Empty;
			Boolean downlink = false;

			while (CommandProcessResponses)
			{

				try
				{
#if DIAGNOSTICS
					Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} ReadLine before");
#endif
					line = serialDevice.ReadLine();
#if DIAGNOSTICS
					Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} ReadLine after:{line}");
#endif

					// See if device successfully joined network
					if (line.StartsWith("+JOIN: Network joined"))
					{
						OnJoinCompletion?.Invoke(true);

						continue;
					}

					if (line.StartsWith("+JOIN: Join failed"))
					{
						OnJoinCompletion?.Invoke(false);

						continue;
					}

					// See if response was what we were expecting as was most probably a happy one
					if (string.Compare(line, CommandExpectedResponse, false) == 0)
					{
						result = Result.Success;
						atExpectedEvent.Set();
						continue;
					}

					// Receive downlink message
					if (line.StartsWith("+MSGHEX: PORT: ") || line.StartsWith("+CMSGHEX: PORT: "))
					{
#if DIAGNOSTICS
						Debug.WriteLine($"{DateTime.UtcNow:HH:mm:ss} Downlink payload :{line}");
#endif
						// TODO validate parsing
						string[] payloadFields = line.Split(':', ';');
						port = int.Parse(payloadFields[2]);
						payload = payloadFields[4].Trim(' ', '"');

						downlink = true;
					}

					// Process the metrics
					if (line.StartsWith("+MSGHEX: RX") || line.StartsWith("+CMSGHEX: RX"))
					{
#if DIAGNOSTICS
						Debug.WriteLine($"{DateTime.UtcNow:HH:mm:ss} Uplink confirm:{line}");
#endif
						// TODO validate parsing
						string[] metricsFields = line.Split(':', ',', ' ');
						rssi = int.Parse(metricsFields[5]);
						snr = double.Parse(metricsFields[8]);

						OnMessageConfirmation?.Invoke(rssi, snr);

						if (downlink)
						{
							downlink = false;

							OnReceiveMessage?.Invoke(port, rssi, snr, payload);
						}
					}

					// Check for error messages/codes
					switch (line)
					{
						case "+MSG: LoRaWAN modem is busy":
						case "+CMSG: LoRaWAN modem is busy":
							result = Result.ModemIsBusy;
							break;

						case "+MSGHEX: Please join network first":
						case "+CMSGHEX: Please join network first":
							result = Result.NetworkNotJoined;
							break;
						case "+JOIN: LoRaWAN modem is busy":
							result = Result.NetworkAlreadyJoined;
							break;
						case "+JOIN: Joined already":
							result = Result.NetworkAlreadyJoined;
							break;
						default:
							break;
					};

					if (line.EndsWith(" ERROR(-1)"))
					{
						result = Result.ParameterIsInvalid; //Y
					}

					if (line.EndsWith(" ERROR(-10)"))
					{
						result = Result.CommandIsUnknown; //Y
					}

					if (line.EndsWith(" ERROR(-11)"))
					{
						result = Result.CommandIsInWrongFormat;
					}

					if (line.EndsWith(" ERROR(-12)"))
					{
						result = Result.CommandIsUnavilableInCurrentMode; //Y
					}

					if (line.EndsWith(" ERROR(-20)"))
					{
						result = Result.TooManyParameters;
					}

					if (line.EndsWith(" ERROR(-21)"))
					{
						result = Result.CommandIsTooLong;
					}

					if (line.EndsWith(" ERROR(-22)"))
					{
						result = Result.ReceiveEndSymbolTimeout;
					}

					if (line.EndsWith(" ERROR(-23)"))
					{
						result = Result.InvalidCharacterReceived;
					}

					if (line.EndsWith(" ERROR(-24)"))
					{
						result = Result.CommandError;
					}


					if (result != Result.Undefined)
					{
						atExpectedEvent.Set();
					}
				}
				catch (TimeoutException)
				{
					// Intentionally ignored, not certain this is a good idea
				}
			}
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
			CommandProcessResponses = false;

			if (CommandResponsesProcessorThread != null)
			{
				CommandResponsesProcessorThread.Join();
				CommandResponsesProcessorThread = null;
			}

			if (serialDevice != null)
			{
				serialDevice.Dispose();
				serialDevice = null;
			}
		}
	}
}
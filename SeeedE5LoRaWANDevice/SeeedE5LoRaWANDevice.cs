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
#if DIAGNOSTICS
	using System.Diagnostics;
#endif
	using System.IO.Ports;
	using System.Text;
	using System.Threading;

	/// <summary>
	/// The LoRaWAN device classes. From The Things Network definitions
	/// </summary>
	public enum LoRaWANDeviceClass
	{
		Undefined = 0,
		/// <summary>
		/// Class A devices support bi-directional communication between a device and a gateway. Uplink messages (from 
		/// the device to the server) can be sent at any time. The device then opens two receive windows at specified 
		/// times (RX1 Delay and RX2 Delay) after an uplink transmission. If the server does not respond in either of 
		/// these receive windows, the next opportunity will be after the next uplink transmission from the device. 
		A,
		/// <summary>
		/// Class B devices extend Class A by adding scheduled receive windows for downlink messages from the server. 
		/// Using time-synchronized beacons transmitted by the gateway, the devices periodically open receive windows. 
		/// The time between beacons is known as the beacon period, and the time during which the device is available 
		/// to receive downlinks is a “ping slot.”
		/// </summary>
		B,
		/// <summary>
		/// Class C devices extend Class A by keeping the receive windows open unless they are transmitting, as shown 
		/// in the figure below. This allows for low-latency communication but is many times more energy consuming than 
		/// Class A devices.
		/// </summary>
		C
	}

	/// <summary>
	/// Possible results of library methods (combination of Seeed LoRa E5 AT command and state machine errors)
	/// </summary>
	public enum Result
	{
		Undefined = 0,
		/// <summary>
		/// Command executed without error.
		/// </summary>
		Success,
		/// <summary>
		/// Command failed to complete in configured duration.
		/// </summary>
		Timeout,

		// Section AT Command Specification Document section 2.4, copy n paste so text might be a bit odd
		/// <summary>
		///  LoRaWAN transaction service is ongoing.
		/// </summary>
		ModemIsBusy,
		/// <summary>
		/// LoRaWAN modem is in OTAA mode and not joined a network.
		/// </summary>
		NetworkNotJoined,
		/// <summary>
		/// Current DR set data rate is not supported.
		/// </summary>
		DataRateError,

		// Section AT Command Specification Document section 4.23, copy n paste so text might be a bit odd
		/// <summary>
		/// LoRaWAN modem is joining a network. 
		/// </summary>
		NetworkJoinInProgress,
		/// <summary>
		/// LoRaWAN modem has already joined a network.
		/// </summary>
		
		// Section AT Command Specification Document section 4.5.2
		NetworkAlreadyJoined,
		/// <summary>
		/// All configured channels are occupied by others.
		/// </summary>
		NoFreeChannels,

		// Not certain how to handle these
		// +MSG: No band in 13469ms 
		// +MSG: Length error N mpt

		// Section AT Command Specification Document section 2.4, copy n paste so text might be a bit odd
		/// <summary>
		/// The input parameter of the command is invalid.
		/// </summary>
		ParameterIsInvalid,
		/// <summary>
		/// Command unknown
		/// </summary>
		CommandIsUnknown,
		/// <summary>
		/// Command is in wrong format
		/// </summary>
		CommandIsInWrongFormat,
		/// <summary>
		/// Command is unavailable in current mode (Check with "AT+MODE")
		/// </summary>
		CommandIsUnavilableInCurrentMode,
		/// <summary>
		/// Too many parameters. LoRaWAN modem support max 15 parameters
		/// </summary>
		CommandHasTooManyParameters,
		/// <summary>
		/// Length of command is too long (exceed 528 bytes)
		/// </summary>
		CommandIsTooLong,
		/// <summary>
		/// Receive end symbol timeout, command must end with <LF>
		/// </summary>
		ReceiveEndSymbolTimeout,
		/// <summary>
		/// Invalid character received
		/// </summary>
		InvalidCharacterReceived,
		/// <summary>
		/// Either InvalidCharacterReceived, ReceiveEndSymbolTimeout, CommandIsTooLong
		/// </summary>
		CommandError
	}

	public class SeeedE5LoRaWANDevice : IDisposable
	{
		public const ushort RegionIDLength = 5;
		/// <summary>
		/// The JoinEUI(formerly known as AppEUI) is a 64-bit globally-unique Extended Unique Identifier (EUI-64).Each 
		/// Join Server, which is used for authenticating the end-devices, is identified by a 64-bit globally unique 
		/// identifier, JoinEUI, that is assigned by either the owner or the operator of that server. This is 
		/// represented by a 16 character long string.
		/// </summary>
		public const ushort AppEuiLength = 16;
		/// <summary>
		/// The AppKey is the encryption key between the source of the message (based on the DevEUI) and the destination 
		/// of the message (based on the AppEUI). This key must be unique for each device. This is represented by a 32 
		/// character long string
		/// </summary>
		public const ushort AppKeyLength = 32;
		/// <summary>
		/// The DevAddr is composed of two parts: the address prefix and the network address. The address prefix is 
		/// allocated by the LoRa Alliance® and is unique to each network that has been granted a NetID. This is 
		/// represented by an 8 character long string.
		/// </summary>
		public const ushort DevAddrLength = 8;
		/// <summary>
		/// After activation, the Network Session Key(NwkSKey) is used to secure messages which do not carry a payload.
		/// </summary>
		public const ushort NwsKeyLength = 32;
		/// <summary>
		/// The AppSKey is an application session key specific for the end-device. It is used by both the application 
		/// server and the end-device to encrypt and decrypt the payload field of application-specific data messages.
		/// This is represented by an 32 character long string
		/// </summary>
		public const ushort AppsKeyLength = 32;
		/// <summary>
		/// The minimum supported port number. Port 0 is used for FRMPayload which contains MAC commands only.
		/// </summary>
		public const ushort MessagePortMinimumValue = 1;
		/// <summary>
		/// The maximum supported port number. Port 224 is used for the LoRaWAN Mac layer test protocol. Ports 
		/// 223…255 are reserved for future application extensions.
		/// </summary>
		public const ushort MessagePortMaximumValue = 223;
		public const ushort MessageBytesMaximumLength = 242;


		private SerialPort serialDevice = null;
		private const int CommandTimeoutDefaultmSec = 1500;
		private const int ReceiveTimeoutDefaultmSec = 10000;
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

		/// <summary>
		/// Initializes a new instance of the devMobile.IoT.LoRaWAN.NetCore.RAK3172.Rak3172LoRaWanDevice class using the
		/// specified port name, baud rate, parity bit, data bits, and stop bit.
		/// </summary>
		/// <param name="serialPortId">The port to use (for example, COM1).</param>
		/// <param name="baudRate">The baud rate, 600 to 115K2.</param>
		/// <param name="serialParity">One of the System.IO.Ports.SerialPort.Parity values, defaults to None.</param>
		/// <param name="dataBits">The data bits value, defaults to 8.</param>
		/// <param name="stopBits">One of the System.IO.Ports.SerialPort.StopBits values, defaults to One.</param>
		/// <exception cref="System.IO.IOException">The serial port could not be found or opened.</exception>
		/// <exception cref="UnauthorizedAccessException">The application does not have the required permissions to open the serial port.</exception>
		/// <exception cref="ArgumentNullException">The serialPortId is null.</exception>
		/// <exception cref="ArgumentException">The specified serialPortId, baudRate, serialParity, dataBits, or stopBits is invalid.</exception>
		/// <exception cref="InvalidOperationException">The attempted operation was invalid e.g. the port was already open.</exception>
		/// <returns><see cref="Result"/> of the operation.</returns>
		public Result Initialise(string serialPortId, int baudRate, Parity serialParity = Parity.None, ushort dataBits = 8, StopBits stopBitCount = StopBits.One)
		{
			serialDevice = new SerialPort(serialPortId);

			// set parameters
			serialDevice.BaudRate = baudRate;
			serialDevice.Parity = serialParity;
			serialDevice.StopBits = stopBitCount;
			serialDevice.Handshake = Handshake.None;
			serialDevice.DataBits = dataBits;
			serialDevice.NewLine = "\r\n";

			serialDevice.ReadTimeout = ReceiveTimeoutDefaultmSec;

			CommandExpectedResponse = string.Empty;

			serialDevice.Open();
			// clear out the input buffer.
			serialDevice.ReadExisting();

			// Only start up the serial port polling thread if the port opened successfuly
			CommandResponsesProcessorThread = new Thread(SerialPortProcessor);
			CommandResponsesProcessorThread.Start();

#if DIAGNOSTICS
         Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+NWM=1");
#endif
			// Ignoring the return from this is intentional
			Result result = SendCommand("+LOWPOWER: WAKEUP", "AT+LOWPOWER: WAKEUP");
			if (result != Result.Success)
			{
#if DIAGNOSTICS
            Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} AT+LOWPOWER: WAKEUP failed {result}");
#endif
			}

			return Result.Success;
		}

		/// <summary>
		/// Sets the LoRaWAN device class.
		/// </summary>
		/// <param name="loRaClass" cref="LoRaWANDeviceClass">The LoRaWAN device class</param>
		/// <exception cref="System.IO.ArgumentException">The loRaClass is invalid.</exception>
		/// <returns><see cref="Result"/> of the operation.</returns>
		public Result Class(LoRaWANDeviceClass loRaClass)
		{
			string command;
			string response;

			switch (loRaClass)
			{
				case LoRaWANDeviceClass.A:
					command = "AT+CLASS=A";
					response = "+CLASS: A";
					break;
				case LoRaWANDeviceClass.B:
					command = "AT+CLASS=B";
					response = "+CLASS: B";
					break;
				case LoRaWANDeviceClass.C:
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

			if (devAddr == null)
			{
				throw new ArgumentNullException(nameof(devAddr));
			}

			if (devAddr.Length != DevAddrLength)
			{
				throw new ArgumentException($"devAddr invalid length must be {DevAddrLength} characters", nameof(devAddr));
			}

			if (nwksKey == null)
			{
				throw new ArgumentNullException(nameof(nwksKey));
			}

			if (nwksKey.Length != NwsKeyLength)
			{
				throw new ArgumentException($"nwksKey invalid length must be {NwsKeyLength} characters", nameof(nwksKey));
			}

			if (appsKey == null)
			{
				throw new ArgumentNullException(nameof(appsKey));
			}

			if (appsKey.Length != AppsKeyLength)
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
			// Sction 4.3 of AT Command document, the returned AppEUI format has :'s
			// +ID: DevAddr, xx:xx:xx:xx
			// Not proud of this, couldn't think of another way todo this which wasn't less obvious.
			// I really wanted to keeo DevAddr format consistent across all libraries.
			devAddr = devAddr.Insert(6, ":");
			devAddr = devAddr.Insert(4, ":");
			devAddr = devAddr.Insert(2, ":");

			result = SendCommand($"+ID: DevAddr, {devAddr}", $"AT+ID=DEVADDR,\"{devAddr}\"");
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

			if (appEui == null)
			{
				throw new ArgumentNullException(nameof(appEui));
			}

			if (appEui.Length != AppEuiLength)
			{
				throw new ArgumentException($"appEui invalid length must be {AppEuiLength} characters", nameof(appEui));
			}

			if (appKey == null)
			{
				throw new ArgumentNullException(nameof(appKey));
			}

			if (appKey.Length != AppKeyLength)
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
			// Sction 4.3 of AT Command document, the returned AppEUI format has :'s
			// +ID: AppEui, xx:xx:xx:xx:xx:xx:xx:xx
			// Not proud of this, couldn't think of another way todo this which wasn't less obvious.
			// I really wanted to keeo AppEUI format consistent across all libraries.
			appEui = appEui.Insert(14, ":");
			appEui = appEui.Insert(12, ":");
			appEui = appEui.Insert(10, ":");
			appEui = appEui.Insert(8, ":");
			appEui = appEui.Insert(6, ":");
			appEui = appEui.Insert(4, ":");
			appEui = appEui.Insert(2, ":");

			result = SendCommand($"+ID: AppEui, {appEui}", $"AT+ID=APPEUI,\"{appEui}\"");
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
			Debug.WriteLine($" {DateTime.UtcNow:hh:mm:ss} Send payload {BytesToHex(payloadBytes)}");
#endif
			return Send(BytesToHex(payloadBytes), confirmed);
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

			this.result = Result.Undefined;
			this.CommandExpectedResponse = expectedResponse;

			serialDevice.WriteLine(command);

			this.atExpectedEvent.Reset();

			if (!this.atExpectedEvent.WaitOne(CommandTimeoutDefaultmSec, false))
				return Result.Timeout;

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
						case "+MSG: DR error":
						case "+CMSG: DR error":
							result = Result.DataRateError;
							break;
						case "+MSGHEX: Please join network first":
						case "+CMSGHEX: Please join network first":
							result = Result.NetworkNotJoined;
							break;
						case "+JOIN: LoRaWAN modem is busy":
							result = Result.NetworkJoinInProgress;
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
						result = Result.CommandHasTooManyParameters;
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

					if (line.EndsWith(" ERROR(-70)"))
					{
						result = Result.NoFreeChannels;
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

		/// <summary>
		/// Converts an array of byes to a hexadecimal string.
		/// </summary>
		/// <param name="payloadBytes"></param>
		/// <exception cref="ArgumentNullException">The array of bytes is null.</exception>
		/// <returns>String containing hex encoded bytes</returns>
		public static string BytesToHex(byte[] payloadBytes)
		{
			if (payloadBytes == null)
			{
				throw new ArgumentNullException(nameof(payloadBytes));
			}

			return BitConverter.ToString(payloadBytes).Replace("-", "");
		}

		/// <summary>
		/// Converts a hexadecimal string to an array of bytes.
		/// </summary>
		/// <param name="payload">array of bytes encoded as hex</param>
		/// <exception cref="ArgumentNullException">The Hexadecimal string is null.</exception>
		/// <exception cref="ArgumentException">The Hexadecimal string is not at even number of characters.</exception>
		/// <exception cref="System.FormatException">The Hexadecimal string contains some invalid characters.</exception>
		/// <returns>Array of bytes parsed from Hexadecimal string.</returns>
		public static byte[] HexToByes(string payload)
		{
			if (payload == null)
			{
				throw new ArgumentNullException(nameof(payload));
			}
			if (payload.Length % 2 != 0)
			{
				throw new ArgumentException($"Payload invalid length must be an even number", nameof(payload));
			}

			Byte[] payloadBytes = new byte[payload.Length / 2];

			char[] chars = payload.ToCharArray();

			for (int index = 0; index < payloadBytes.Length; index++)
			{
				byte byteHigh = Convert.ToByte(chars[index * 2].ToString(), 16);
				byte byteLow = Convert.ToByte(chars[(index * 2) + 1].ToString(), 16);

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
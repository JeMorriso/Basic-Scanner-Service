﻿using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;

using Motorola.Snapi;
using Motorola.Snapi.Constants.Enums;
using Motorola.Snapi.Constants;
using Motorola.Snapi.EventArguments;

using Renci.SshNet;

using Newtonsoft.Json;

using log4net;
using log4net.Config;
using System.Reflection;

namespace ZebraScannerService
{
	// class handles current scanner info related to inventory
	public class ScannerInfo
	{
		public IMotorolaBarcodeScanner scanner;
		//public int prefix;
		public ScannerTimer timer;
		public Tuple<string, BarcodeType> prevScan;
	}

	// timer class accepts scanner id and led mode so event handler has access to these fields
	public class ScannerTimer : System.Timers.Timer
	{
		public int scannerId;
		// needed for ledtimer, but not scantimer
		public LedMode? ledOff;
	}

	public enum BarcodeType { nid, location, multiLocation, None };

	class ScannerService
	{
		private static ConnectionInfo ConnInfo;
		private static ILog log;
		private static SshClient sshClient;

		private static Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>> notifications;
		//private static Dictionary<int, int> prefixes;
		//private static Dictionary<int, ScannerInfo> scanners;
		//private static Dictionary<int, ScannerInfo> oldScanners;

		private static ScannerInfo scannerInfo;
		private static ScannerInfo oldScannerInfo;

		private static Dictionary<string, string> inventoryServer;

		private const int timerInterval = 30000;

		// see autoscan.py on inventory server for what products these correspond to
		private static List<string> nidPrefixes = new List<string>() { "T1", "T2", "T3", "T4", "T5", "T6", "TQ", "X1", "1", "2", "3", "4" };

		//set behaviour of scanners
		public static void ConfigureScanners()
		{
			//int asciiPrefix = 0x61;

			// no way to tell which scanner disconnects so reset everything whenever any scanner disconnects
			//prefixes = new Dictionary<int, int>();
			//scanners = new Dictionary<int, ScannerInfo>();

			List<IMotorolaBarcodeScanner> deviceList = BarcodeScannerManager.Instance.GetDevices();
			// Ensure scanners are attached
			if (deviceList.Count == 0)
			{
				log.Error("No scanners are attached.");
				Console.WriteLine("No scanners are attached.");
				return;
			}
			// want: multipoint-point, low volume, beep / flash green on good decode, mode IBMHID, assign prefix
			foreach (var scanner in deviceList)
			{
				// if cradle
				if (scanner.Info.ModelNumber == "CR0078-SC10007WR")
				{
					scanner.SetHostMode(HostMode.USB_IBMHID);

					// Set cradle to multipoint-to-point mode, so that up to 3 scanners can be linked to it.
//scanner.Actions.StoreIBMAttribute(538, DataType.Bool, true);

					log.Debug("Setting hostmode=USB_IBMHID for scanner serial=" + scanner.Info.SerialNumber);
					Console.WriteLine("Setting hostmode=USB_IBMHID for scanner serial=" + scanner.Info.SerialNumber);
				}
				// if scanner
				else
				{
					// Set beeper volume low
					scanner.Actions.StoreIBMAttribute(140, DataType.Byte, BeeperVolume.Low);
					// Set beeper tone medium
					scanner.Actions.StoreIBMAttribute(145, DataType.Byte, 2);

					// Set beep on BarcodeScanEvent - LED also flashes green (unable to change)
					scanner.Actions.StoreIBMAttribute(56, DataType.Bool, true);
					// Disable laser flashing on BarcodeScanEvent
					scanner.Actions.StoreIBMAttribute(859, DataType.Byte, 0);

					//	Enable barcode prefix
					//scanner.Actions.StoreIBMAttribute(235, DataType.Byte, 4);
					//	scanner.Actions.StoreIBMAttribute(99, DataType.Array, 1);
					//	assign letter prefix for identification

					//   scanner.Actions.StoreIBMAttribute(105, DataType.Array, asciiPrefix);

					//prefixes.Add(asciiPrefix, scanner.Info.ScannerId);

					scannerInfo = new ScannerInfo
					{
						scanner = scanner,
						//prefix = asciiPrefix,
						timer = null,
						prevScan = null
					};
					// restore saved information for matching scanner
					if (oldScannerInfo != null)
					{
						Console.WriteLine("old scanner detected");
						scannerInfo.timer = oldScannerInfo.timer;
						scannerInfo.prevScan = oldScannerInfo.prevScan;
					}
					log.Debug("Setting default program attributes for scanner serial=" + scanner.Info.SerialNumber);
					//log.Debug("Setting prefix=" + asciiPrefix + " for scanner serial=" + scanner.Info.SerialNumber);
					Console.WriteLine("Setting default program attributes for scanner serial=" + scanner.Info.SerialNumber);
					//Console.WriteLine("Setting prefix=" + asciiPrefix + " for scanner serial=" + scanner.Info.SerialNumber);
					//asciiPrefix++;
				}
			}
			// assumes that only 1 scanner disconnected.
			oldScannerInfo = null;
		}

		//		private static void RestoreScannerInfo(ScannerInfo scannerInfo)
		//		{
		//			foreach (ScannerInfo oldScannerInfo in oldScanners.Values)
		//			{
		//				if (oldScannerInfo.scanner.Info.SerialNumber == scannerInfo.scanner.Info.SerialNumber)
		//				{
		//					Console.WriteLine("Scanner info restored after disconnect for scanner serial=" + scannerInfo.scanner.Info.SerialNumber);
		//					log.Debug("Scanner info restored after disconnect for scanner serial=" + scannerInfo.scanner.Info.SerialNumber);
		//					//Console.WriteLine("scanner serial: " + scannerInfo.scanner.Info.SerialNumber);
		//					//Console.WriteLine("old scanner ID: " + oldScannerInfo.scanner.Info.ScannerId);
		//					//Console.WriteLine("new scanner ID: " + scannerInfo.scanner.Info.ScannerId);

		//					scannerInfo.prevScan = oldScannerInfo.prevScan;
		//					scannerInfo.timer = oldScannerInfo.timer;
		//					// need to change the timer stored ID to the new scanner ID
		//					scannerInfo.timer.scannerId = scannerInfo.scanner.Info.ScannerId;
		//					return;
		//				}
		//			}
		//		}

		public void Start()
		{
			// Setup logging
			log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
			XmlConfigurator.Configure();

			if (!(BarcodeScannerManager.Instance.Open()))
			{
				log.Fatal("Failed to open CoreScanner driver");
				Console.WriteLine("Failed to open CoreScanner driver");
			}
			else
			{
				log.Debug("CoreScanner driver instance opened");
				Console.WriteLine("CoreScanner driver instance opened");
			}

			// read inventory server info from file
			var text = File.ReadAllText("ScannerConfig.json");
			inventoryServer = JsonConvert.DeserializeObject<Dictionary<string, string>>(text);

			//Setup SSH connection info for remote inventory database access
			ConnInfo = new ConnectionInfo(inventoryServer["hostname"], inventoryServer["username"],
			   new AuthenticationMethod[] {
					// Password based Authentication
					new PasswordAuthenticationMethod(inventoryServer["username"],inventoryServer["password"])
			   }
			);
			sshClient = new SshClient(ConnInfo);
			CheckSSHConnection();
			log.Debug("Added SSH connection info: " + inventoryServer["username"] + "@" + inventoryServer["hostname"]);
			Console.WriteLine("Added SSH connection info: " + inventoryServer["username"] + "@" + inventoryServer["hostname"]);

			BarcodeScannerManager.Instance.RegisterForEvents(EventType.Barcode, EventType.Pnp);
			BarcodeScannerManager.Instance.DataReceived += OnDataReceived;
			BarcodeScannerManager.Instance.ScannerAttached += OnScannerAttached;
			BarcodeScannerManager.Instance.ScannerDetached += OnScannerDetached;

			log.Debug("Subscribed for events in BarcodeScannerManager: CCoreScanner.Barcode, CCoreScanner.Pnp");
			log.Debug("Subscribed for events in Main: BarcodeScannerManager.ScannerAttached, BarcodeScannerManager.ScannerDetached");
			Console.WriteLine("Subscribed for events in BarcodeScannerManager: CCoreScanner.Barcode, CCoreScanner.Pnp");
			Console.WriteLine("Subscribed for events in Main: BarcodeScannerManager.ScannerAttached, BarcodeScannerManager.ScannerDetached");

			// Add notification beep flash / sequences to notifications dictionary
			notifications = new Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>>
			{
				{ "tryDatabase", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 1500, (BeepPattern?)null) },
				{ "timerUp", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 50, (BeepPattern?)BeepPattern.TwoLowShort) },
				{ "barcodeFailure", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 100, (BeepPattern?)BeepPattern.OneLowLong) },
				{ "databaseFailure", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 300, (BeepPattern?)BeepPattern.ThreeLowLong) }
			};
			ConfigureScanners();
		}

		public void Stop()
		{
			log.Debug("Zebra Scanner Service stopped");
			Console.WriteLine("Zebra Scanner Service stopped");
			sshClient.Disconnect();
			BarcodeScannerManager.Instance.Close();
		}

		// PnpEventArgs: there is no way of telling which scanner attached / detached
		private static void OnScannerAttached(object sender, PnpEventArgs e)
		{
			log.Debug("Scanner attached");
			Console.WriteLine("Scanner attached");

			// if cradle detaches, when it reattaches this event handler gets called once for cradle, once for scanner.
			// only want to configure one time.
			// this needs to be handled for more scanners. Can set 10 second timer to delete oldScanners
			if (BarcodeScannerManager.Instance.GetDevices().Count > 1)
			{
				oldScannerInfo = scannerInfo;
				// every time scanner is attached, reconfigure all scanners, because there is no guarantee that scanner that has
				// previously detached has the same id when it reattaches, and we don't have the id of the scanner that has attached.
				ConfigureScanners();
			}
		}

		private static void OnScannerDetached(object sender, PnpEventArgs e)
		{
			log.Debug("Scanner detached");
			Console.WriteLine("Scanner detached");
		}

		private static void OnScanTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			// user didn't scan nid in time. 
			// case 9/10 : prevScan defined -> undefined
			log.Error("Timed out waiting for barcode scan event");
			Console.WriteLine("Timed out waiting for barcode scan event");

			SendNotification(((ScannerTimer)source).scannerId, notifications["timerUp"]);
			scannerInfo.prevScan = null;
		}

		// Handles LED timer elapsed event by getting LED colour that was toggled on when LED timer was created, and turning that colour off.
		private static void OnLedTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			LedMode ledOff = (LedMode)((ScannerTimer)source).ledOff;
			IMotorolaBarcodeScanner scanner = scannerInfo.scanner;

			scanner.Actions.ToggleLed(ledOff);
		}

		private static void OnDataReceived(object sender, BarcodeScanEventArgs e)
		{
			Console.WriteLine("Barcode scan detected from scanner id=" + e.ScannerId + ": " + e.Data);
			log.Debug("Barcode scan detected from scanner id=" + e.ScannerId + ": " + e.Data);

			// get prefix identifier and convert from char to int
			//int prefix = Convert.ToInt32(e.Data[0]);
			int scannerId = scannerInfo.scanner.Info.ScannerId;

			// convert to uppercase and strip any whitespace
			string barcode = e.Data.ToUpper().Trim();
			BarcodeType barcodeType = CheckBarcode(barcode);

			if (barcodeType == BarcodeType.None)
			{
				log.Error("Barcode " + e.Data + " not recognized as location or NID");
				Console.WriteLine("Barcode " + e.Data + " not recognized as location or NID");
				SendNotification(scannerId, notifications["barcodeFailure"]);
			}
			else
			{
				// if successful scan, then either stop timer or restart start it, so stop here.
				if (scannerInfo.timer != null)
				{
					scannerInfo.timer.Stop();
				}
				scannerInfo.timer = new ScannerTimer
				{
					Interval = timerInterval,
					AutoReset = false,
					scannerId = scannerId,
					ledOff = null
				};
				scannerInfo.timer.Elapsed += OnScanTimerElapsed;

				log.Debug("Barcode " + barcode + " recognized as type " + barcodeType);
				Console.WriteLine("Barcode " + barcode + " recognized as type " + barcodeType);

				// case 1: prevScan: null		current: nid1 		-> prevScan: nid1		timer: start	()
				// case 2: prevScan: null		current: location1	-> prevScan: location1	timer: start	()	 
				// case 3: prevScan: nid1		current: nid1		-> prevScan: null		timer: stop		(remove nid's location from database)				
				// case 4: prevScan: nid1		current: nid2		-> prevScan: nid2		timer: start	(overwrite previous nid with new prevScan nid)
				// case 5: prevScan: nid1		current: location1	-> prevScan: location1	timer: start	(nid scanned first - overwrite with location)
				// case 6: prevScan: location1	current: location1	-> prevScan: location1	timer: start	(overwrite same location)
				// case 7: prevScan: location1	current: location2 	-> prevScan: location2	timer: start	(overwrite new location)
				// case 8: prevScan: location1	current: nid1 		-> prevScan: null		timer: stop		(update nid's location in database)

				// NOTE: BarcodeType.multiLocation added. Behaves same as location except do not delete prevScan in case 8.

				// cases 1 and 2
				if (scannerInfo.prevScan == null)
				{
					scannerInfo.timer.Start();
					scannerInfo.prevScan = Tuple.Create(barcode, barcodeType);
				}
				// cases 5,6,7
				else if (barcodeType == BarcodeType.location || barcodeType == BarcodeType.multiLocation)
				{
					scannerInfo.timer.Start();
					scannerInfo.prevScan = Tuple.Create(barcode, barcodeType);
				}
				else
				{
					if (scannerInfo.prevScan.Item2 == BarcodeType.nid)
					{
						// case 3
						if (barcode.Equals(scannerInfo.prevScan.Item1))
						{
							SendNotification(scannerId, notifications["tryDatabase"]);
							UpdateDatabase(scannerId, barcode);
							scannerInfo.prevScan = null;
						}
						// case 4
						else
						{
							scannerInfo.timer.Start();
							scannerInfo.prevScan = Tuple.Create(barcode, barcodeType);
						}
					}
					// case 8 (prevScan is location or multiLocation)
					else
					{
						SendNotification(scannerId, notifications["tryDatabase"]);
						string location = scannerInfo.prevScan.Item1;
						UpdateDatabase(scannerId, barcode, location);
						// if multiple items are allowed, then do not wipe location
						if (scannerInfo.prevScan.Item2 == BarcodeType.location)
						{
							scannerInfo.prevScan = null;
						}
						// if at portapillars, location only gets wiped when scanner times out. Each NID scanned restarts timer.
						else
						{
							scannerInfo.timer.Start();
						}
					}
				}
			}
		}

		// returns "nid" if barcode scanned is recognized as NID, and "location" if recognized as location
		public static BarcodeType CheckBarcode(string barcode)
		{
			string locationFormat = @"^P[NESW]\d{4}";
			string portapillarFormat = @"^PMM\d0{4}$";
			string storageFormat = @"^S\d\d$";
			string nidFormat = @"^(\d|[A-F]){10}$";
			string ertFormat = @"^\d{8}$";

			//Console.WriteLine("BARCODE: " + barcode);
			if (EvalRegex(locationFormat, barcode))
			{
				return BarcodeType.location;
			}
			else if (EvalRegex(portapillarFormat, barcode) || EvalRegex(storageFormat, barcode))
			{
				//Console.WriteLine("found multi");
				return BarcodeType.multiLocation;
			}
			else
			{
				// longest NIDs have 2 digit prefix + 10 digit NID, shortest NIDs are ERTs, 8 digits
				if (barcode.Length > 12 || barcode.Length < 8)
				{
					return BarcodeType.None;
				}
				// remove and check NID prefix - if barcode length > 10, digits at front should only be prefixes
				if (barcode.Length > 10)
				{
					string nidPrefix = barcode.Substring(0, barcode.Length - 10);
					barcode = barcode.Substring(barcode.Length - 10);

					if (!nidPrefixes.Contains(nidPrefix))
					{
						log.Error("Unknown NID prefix=" + nidPrefix);
						Console.WriteLine("Unknown NID prefix=" + nidPrefix);
						return BarcodeType.None;
					}
				}
				// ert NID is just treated as regular NID by this program
				if (EvalRegex(nidFormat, barcode) || EvalRegex(ertFormat, barcode))
				{
					return BarcodeType.nid;
				}
				else
				{
					return BarcodeType.None;
				}
			}
		}

		public static Boolean EvalRegex(string rxStr, string matchStr)
		{
			Regex rx = new Regex(rxStr);
			Match match = rx.Match(matchStr);

			return match.Success;
		}

		public static void SendNotification(int scannerId, Tuple<LedMode?, LedMode?, int, BeepPattern?> notificationParams)
		{
			IMotorolaBarcodeScanner scanner = scannerInfo.scanner;

			// sound beeper
			if (notificationParams.Item4 != null)
			{
				scanner.Actions.SoundBeeper((BeepPattern)notificationParams.Item4);
			}
			// flash LED
			if (notificationParams.Item1 != null && notificationParams.Item2 != null)
			{
				scanner.Actions.ToggleLed((LedMode)notificationParams.Item1);
				// start timer, and when timer is up, event handler turns off LED
				var _ledTimer = new ScannerTimer
				{
					Interval = notificationParams.Item3,
					AutoReset = false,
					scannerId = scanner.Info.ScannerId,
					ledOff = (LedMode)notificationParams.Item2
				};
				_ledTimer.Elapsed += OnLedTimerElapsed;
				_ledTimer.Start();
			}
		}

		// ensure SSH tunnel is established. If can't connect log error and stop service
		public static bool CheckSSHConnection()
		{
			if (!sshClient.IsConnected)
			{
				try
				{
					sshClient.Connect();
				}
				// not sure what exception type will be thrown
				catch (Exception e)
				{
					log.Fatal("Error establishing connection to inventory server. Exception: " + e);
					Console.WriteLine("Error establishing connection to inventory server. Exception: " + e);
					return false;
				}
			}
			return true;
		}

		public static void UpdateDatabase(int scannerId, string nid, string location = null)
		{
			if (!CheckSSHConnection())
			{
				SendNotification(scannerId, notifications["databaseFailure"]);
				return;
			}
			using (var cmd = sshClient.CreateCommand("python3 " + inventoryServer["inventory_script"] + " " + nid + " " + location))
			{
				cmd.Execute();

				// problem occurred
				if (cmd.ExitStatus > 0 && cmd.ExitStatus < 10)
				{
					SendNotification(scannerId, notifications["databaseFailure"]);
					// could not connect to database, or could not commit to database, or something unexpected has occurred
					if (cmd.ExitStatus == 1)
					{
						Console.WriteLine("Error connecting to database.");
						log.Fatal("Error connecting to database.");
					}
					else if (cmd.ExitStatus == 2 || cmd.ExitStatus > 0)
					{
						if (location != null)
						{
							log.Fatal("Error updating database with location=" + location + ", NID=" + nid);
							Console.WriteLine("Error updating database with location=" + location + ", NID=" + nid);
						}
						else
						{
							log.Fatal("Error removing NID=" + nid + " location info from database");
							Console.WriteLine("Error removing NID=" + nid + " location info from database");
						}
					}
				}
				// no problem occurred
				else
				{
					if (location != null)
					{
						log.Debug("Successfully updated database with location=" + location + ", NID=" + nid);
						Console.WriteLine("Successfully updated database with location=" + location + ", NID=" + nid);
					}
					else
					{
						if (cmd.ExitStatus != 10)
						{
							log.Debug("Successfully removed location info for NID=" + nid);
							Console.WriteLine("Successfully removed location info for NID=" + nid);
						}
						else
						{
							log.Debug("Attempted remove location info for NID=" + nid + ", but device is not in database.");
							Console.WriteLine("Attempted remove location info for NID=" + nid + ", but device is not in database.");
						}
					}
				}
			}
		}
	}
}

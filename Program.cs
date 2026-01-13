using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using TapoConnect;
using TapoConnect.Dto;

namespace TapoToggle
{
    class Program
    {
        static async Task Main(string[] args)
        {
            ConsoleLog("--- TapoToggle ---", ConsoleColor.Cyan);

            string email, password, deviceLabel;
            var isInteractive = args.Length == 0;

            if (!isInteractive)
            {
                var input = args[0];
                var configFile = input.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? input : $"{input}.json";

                if (!File.Exists(configFile))
                {
                    ConsoleLog($"   Error: Configuration file '{configFile}' not found.", ConsoleColor.Red);
                    return;
                }

                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile(configFile, optional: false, reloadOnChange: true)
                    .Build();

                email = config["TapoConfig:Email"] ?? "";
                password = config["TapoConfig:Password"] ?? "";
                deviceLabel = config["TapoConfig:DeviceLabel"] ?? "";

                ConsoleLog($"   Using configuration: {configFile}", ConsoleColor.Gray);
            }
            else
            {
                var defaultConfig = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("config.json", optional: true)
                    .Build();

                email = defaultConfig["TapoConfig:Email"] ?? "";
                password = defaultConfig["TapoConfig:Password"] ?? "";
                deviceLabel = ""; // Will be chosen via menu

                if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                {
                    ConsoleLog("   No configuration file provided and default credentials missing.",
                        ConsoleColor.Yellow);
                    Console.Write("   Enter Tapo Email: ");
                    email = Console.ReadLine() ?? "";
                    Console.Write("   Enter Tapo Password: ");
                    password = ReadPassword();
                }
            }

            try
            {
                // STEP 1: Cloud Authentication
                ConsoleLog("\n1. Authenticating with Tapo Cloud...");
                var cloudClient = new TapoCloudClient();
                var cloudResult = await cloudClient.LoginAsync(email, password);
                ConsoleLog("   Cloud Login Successful.", ConsoleColor.Gray);

                // STEP 2: Device Retrieval/Selection
                var deviceList = await cloudClient.ListDevicesAsync(cloudResult.Token);
                TapoDeviceDto? selectedDevice = null;

                if (isInteractive)
                {
                    selectedDevice = ShowDeviceMenu(deviceList.DeviceList.ToList());
                }
                else
                {
                    ConsoleLog($"2. Searching for device with label: '{deviceLabel}'...");
                    selectedDevice = deviceList.DeviceList.FirstOrDefault(d =>
                        d.Alias.Equals(deviceLabel, StringComparison.OrdinalIgnoreCase));
                }

                if (selectedDevice == null)
                {
                    ConsoleLog("   Error: No device selected or found.", ConsoleColor.Yellow);
                    return;
                }

                ConsoleLog($"   Selected: {selectedDevice.Alias} (MAC: {selectedDevice.DeviceMac})", ConsoleColor.Gray);

                // STEP 3: Network Discovery
                ConsoleLog("3. Resolving Local IP...");
                await PreScanSubnetAsync();

                var resolvedIp = await DiscoverIpByMacAsync(selectedDevice.DeviceMac);

                if (string.IsNullOrEmpty(resolvedIp))
                {
                    ConsoleLog("   UDP Discovery timed out. Falling back to ARP table...", ConsoleColor.Gray);
                    resolvedIp = ResolveIpFromMac(selectedDevice.DeviceMac);
                }

                if (string.IsNullOrEmpty(resolvedIp))
                {
                    ConsoleLog("   Error: Could not find the device on the local network.", ConsoleColor.Yellow);
                    return;
                }

                ConsoleLog($"   Resolved Local IP: {resolvedIp}", ConsoleColor.Green);

                // STEP 4: Local Login
                ConsoleLog($"4. Attempting local login to {resolvedIp}...");
                var deviceClient = new TapoDeviceClient();
                var deviceKey = await deviceClient.LoginByIpAsync(resolvedIp, email, password);
                ConsoleLog("   SUCCESS: Local connection established!", ConsoleColor.Green);

                // STEP 5: Toggle Logic
                var info = await deviceClient.GetDeviceInfoAsync(deviceKey);
                var newState = !info.DeviceOn;

                ConsoleLog($"\n   Current State: {(info.DeviceOn ? "ON" : "OFF")}");
                ConsoleLog($"   Toggling device to: {(newState ? "ON" : "OFF")}...");

                await deviceClient.SetPowerAsync(deviceKey, newState);

                var updatedInfo = await deviceClient.GetDeviceInfoAsync(deviceKey);
                ConsoleLog($"\n{updatedInfo.Nickname} is now {(updatedInfo.DeviceOn ? "ON" : "OFF")}",
                    ConsoleColor.Cyan);
            }
            catch (Exception ex)
            {
                ConsoleLog($"\nERROR: {ex.Message}", ConsoleColor.Red);
                if (ex.InnerException != null)
                    ConsoleLog($"Inner Error: {ex.InnerException.Message}", ConsoleColor.Red);
            }

            ConsoleLog("\nPress any key to exit.", ConsoleColor.Gray);
            Console.ReadKey();
        }

        private static TapoDeviceDto? ShowDeviceMenu(List<TapoDeviceDto> devices)
        {
            if (devices.Count == 0) return null;

            ConsoleLog("\n--- Available Devices ---", ConsoleColor.Cyan);
            for (var i = 0; i < devices.Count; i++)
            {
                Console.WriteLine($" {i + 1}. {devices[i].Alias} ({devices[i].DeviceModel})");
            }

            while (true)
            {
                Console.Write($"\nSelect a device (1-{devices.Count}): ");
                if (int.TryParse(Console.ReadLine(), out var choice) && choice >= 1 && choice <= devices.Count)
                {
                    return devices[choice - 1];
                }

                ConsoleLog("Invalid selection.", ConsoleColor.Red);
            }
        }

        private static string ReadPassword()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Remove(sb.Length - 1, 1);
                else if (key.Key != ConsoleKey.Backspace) sb.Append(key.KeyChar);
            }

            Console.WriteLine();
            return sb.ToString();
        }

        private static void ConsoleLog(string message, ConsoleColor colour = ConsoleColor.White)
        {
            Console.ForegroundColor = colour;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        private static string NormaliseMac(string mac)
        {
            return mac.Replace("-", "").Replace(":", "").ToLower();
        }

        private static async Task PreScanSubnetAsync()
        {
            var localIp = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            if (localIp == null) return;

            var ipString = localIp.Address.ToString();
            var baseIp = ipString.Substring(0, ipString.LastIndexOf('.') + 1);

            var tasks = Enumerable.Range(1, 254).Select(async i =>
            {
                try
                {
                    using var p = new Ping();
                    await p.SendPingAsync(baseIp + i, 150);
                }
                catch
                {
                }
            });

            await Task.WhenAll(tasks);
        }

        private static async Task<string?> DiscoverIpByMacAsync(string macAddress)
        {
            var targetMac = NormaliseMac(macAddress);
            var discoveryPacket = Encoding.UTF8.GetBytes("{\"method\":\"discovery\",\"params\":{}}");

            foreach (var address in GetBroadcastAddresses())
            {
                using var udpClient = new UdpClient();
                udpClient.EnableBroadcast = true;
                udpClient.Client.ReceiveTimeout = 1000;

                try
                {
                    var endPoint = new IPEndPoint(address, 20002);
                    await udpClient.SendAsync(discoveryPacket, discoveryPacket.Length, endPoint);

                    var listenTask = udpClient.ReceiveAsync();
                    if (await Task.WhenAny(listenTask, Task.Delay(1500)) == listenTask)
                    {
                        var result = await listenTask;
                        var response = Encoding.UTF8.GetString(result.Buffer).ToLower();

                        if (NormaliseMac(response).Contains(targetMac))
                        {
                            return result.RemoteEndPoint.Address.ToString();
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static IEnumerable<IPAddress> GetBroadcastAddresses()
        {
            var addresses = new List<IPAddress> { IPAddress.Broadcast };
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(c => c.OperationalStatus == OperationalStatus.Up &&
                            c.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in interfaces)
            {
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var ipBytes = ip.Address.GetAddressBytes();
                        var maskBytes = ip.IPv4Mask.GetAddressBytes();
                        var bcBytes = new byte[4];
                        for (var i = 0; i < 4; i++) bcBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
                        addresses.Add(new IPAddress(bcBytes));
                    }
                }
            }

            return addresses.Distinct();
        }

        private static string? ResolveIpFromMac(string macAddress)
        {
            if (string.IsNullOrEmpty(macAddress)) return null;
            var targetMac = NormaliseMac(macAddress);

            try
            {
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var fileName = isWindows ? "arp" : "ip";
                var arguments = isWindows ? "-a" : "neighbor show";

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                foreach (var line in output.Split('\n'))
                {
                    if (NormaliseMac(line).Contains(targetMac))
                    {
                        var match = Regex.Match(line, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b");
                        if (match.Success) return match.Value;
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }
}

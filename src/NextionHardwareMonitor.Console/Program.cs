using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace NextionHardwareMonitor.Console
{
	public class Program
	{
		private const int ProgressBarStartIndex = 0;
		
		public static async Task Main(string[] args)
		{
			await SerialTest();
		}

		private static int GetProgressBarPicId(int value)
		{
			value = Math.Max(0, Math.Min(100, value));
			return (int) Math.Floor((double) value / 5) + ProgressBarStartIndex;
		}

		private static async Task SerialTest()
		{
			var port = await GetSerialPort("COM5");
			ListenToSerial(port);
			
			SendCommand(port.BaseStream, "dim=10");
			
			var counter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
			
			while (true)
			{
				var cpuPercentage = (int)Math.Round(counter.NextValue());

				SendCommand(port.BaseStream,
					$"cpuPrgs.pic={GetProgressBarPicId(cpuPercentage)}",
					$"cpuPrct.val={cpuPercentage}",
					"t1.xcen=1");

				await Task.Delay(1000);
			}
		}

		private static async void ListenToSerial(SerialPort port)
		{
			while (true)
			{
				if (port.BytesToRead <= 0)
				{
					await Task.Delay(100);
					continue;
				}

				var readBuffer = new byte[port.BytesToRead];
				port.Read(readBuffer, 0, readBuffer.Length);
				System.Console.WriteLine("Recieve: " + Encoding.UTF8.GetString(readBuffer));

				//var value = random.Next(0, 999);
				//SendCommand(port, $"n0.val={value}");
			}
		}

		private static async Task<SerialPort> GetSerialPort(string portName, int initialBaudRate = 9600, int newBaudRate = 57600)
		{
			var port = new SerialPort("COM5", initialBaudRate);
			
			port.Open();

			while (!port.IsOpen)
				await Task.Delay(10);

			SendCommand(port.BaseStream, string.Empty);

			if (newBaudRate == initialBaudRate)
				return port;
			
			SendCommand(port.BaseStream, $"baud={newBaudRate}");

			port.Close();
			port.Dispose();
			
			port = new SerialPort("COM5", newBaudRate);
			port.Open();
			
			while (!port.IsOpen)
				await Task.Delay(10);

			return port;
		}

		private static readonly byte[] InterCommand = {0xFF, 0xFF, 0xFF};

		private static void SendCommand(Stream stream, params string[] commands)
		{
			using var commandBytes = new MemoryStream();

			foreach (var command in commands)
			{
				commandBytes.Write(Encoding.UTF8.GetBytes(command));
				commandBytes.Write(InterCommand);
			}
			
			stream.Write(commandBytes.ToArray());
			stream.Flush();
			
			System.Console.WriteLine($"Write: {Encoding.UTF8.GetString(commandBytes.ToArray())}");
		}
	}
}

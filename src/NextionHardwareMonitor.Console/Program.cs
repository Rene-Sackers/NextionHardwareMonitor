using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace NextionHardwareMonitor.Console
{
	public class Program
	{
		public static Task Main(string[] args)
		{
			var instance = new ProgramInstance();
			return instance.Run();
		}
	}

	public class UpdateVisitor : IVisitor
	{
		public void VisitComputer(IComputer computer)
		{
			computer.Traverse(this);
		}
		
		public void VisitHardware(IHardware hardware)
		{
			hardware.Update();
			foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
		}
		
		public void VisitSensor(ISensor sensor) { }

		public void VisitParameter(IParameter parameter) { }
	}

	public class SensorWatcher : IDisposable
	{
		private bool _isOpen;
		private ICollection<WatchedSensor> _watchedSensors;
		private Computer _computer;

		public void Initialize()
		{
			Dispose();
			
			_computer = new Computer
			{
				IsCpuEnabled = true,
				IsGpuEnabled = true,
				IsMemoryEnabled = true,
				IsMotherboardEnabled = true,
				IsControllerEnabled = true,
				IsNetworkEnabled = true,
				IsStorageEnabled = true
			};

			_computer.Open();
			_computer.Accept(new UpdateVisitor());
			
			_isOpen = true;
		}
		
		public void WatchSensors(ICollection<WatchedSensor> sensorsToWatch)
		{
			_watchedSensors = new List<WatchedSensor>(sensorsToWatch);

			IterateSensors(false, sensor =>
			{
				var watchedSensor = sensorsToWatch.FirstOrDefault(s => s.Identifier == sensor.Identifier.ToString());
				if (watchedSensor != null)
					watchedSensor.Sensor = sensor;
			});
			
			foreach (var sensor in _watchedSensors.Where(s => s.Sensor == null))
				System.Console.WriteLine($"Could not find sensor {sensor.Identifier}");
		}

		public void PrintSensors()
		{
			IterateSensors(true);
		}

		private void IterateSensors(bool printToConsole, Action<ISensor> sensorHandler = null)
		{
			foreach (var hardware in _computer.Hardware)
			{
				if (printToConsole)
					System.Console.WriteLine($"[{hardware.Identifier}] Hardware: {hardware.Name}");

				foreach (var subhardware in hardware.SubHardware)
				{
					if (printToConsole)
						System.Console.WriteLine($"\t[{subhardware.Identifier}] Subhardware: {subhardware.Name}");

					foreach (var sensor in subhardware.Sensors)
					{
						sensorHandler?.Invoke(sensor);

						if (printToConsole)
							System.Console.WriteLine($"\t\t[{sensor.Identifier}] Sensor: {sensor.Name}, value: {sensor.Value}");
					}
				}

				foreach (var sensor in hardware.Sensors)
				{
					sensorHandler?.Invoke(sensor);

					if (printToConsole)
						System.Console.WriteLine($"\t[{sensor.Identifier}] Sensor: {sensor.Name}, value: {sensor.Value}");
				}
			}
		}

		public void UpdateWatchedSensors()
		{
			// Update distinct hardware
			_watchedSensors
				.Select(s => s.Sensor.Hardware)
				.GroupBy(h => h.Identifier)
				.Select(g => g.First())
				.ToList()
				.ForEach(h => h.Update());
			
			_watchedSensors
				.ToList()
				.ForEach(s => s.Value = s.Sensor.Value);
		}

		public void Dispose()
		{
			if (_isOpen)
				_computer.Close();

			_isOpen = false;
			_computer = null;
		}
	}

	public class WatchedSensor
	{
		public string Identifier { get; }
		
		public float? Value { get; set; }
		
		public ISensor Sensor { get; set; }

		public WatchedSensor(string identifier)
		{
			Identifier = identifier;
		}
	}

	public class ProgramInstance
	{
		private const int ProgressBarStartIndex = 0;

		private SerialPort _port;
		
		public async Task Run()
		{
			_port = await GetSerialPort("COM5");
			ListenToSerial(_port);
			
			var cpuTempSensor = new WatchedSensor("/amdcpu/0/temperature/2");
			var gpuTempSensor = new WatchedSensor("/gpu-nvidia/0/temperature/0");
			var cpuUsageSensor = new WatchedSensor("/amdcpu/0/load/0");
			var gpuUsageSensor = new WatchedSensor("/gpu-nvidia/0/load/0");
			var ramUsageSensor = new WatchedSensor("/ram/load/0");
			var vramUsageSensor = new WatchedSensor("/gpu-nvidia/0/load/3");

			var watcher = new SensorWatcher();
			
			watcher.Initialize();
			//watcher.PrintSensors();
			//System.Console.ReadKey();

			watcher.WatchSensors(new[]
			{
				cpuTempSensor,
				gpuTempSensor,
				cpuUsageSensor,
				gpuUsageSensor,
				ramUsageSensor,
				vramUsageSensor
			});
			
			SendCommand("dim=50");
			SetDialLabel(1, "CPU °C");
			SetDialLabel(2, "GPU °C");
			SetDialLabel(3, "CPU %");
			SetDialLabel(4, "GPU %");
			SetDialLabel(5, "RAM %");
			SetDialLabel(6, "VRAM %");

			var cancelRequested = false;
			System.Console.CancelKeyPress += (_, _) => cancelRequested = true;

			while (!cancelRequested)
			{
				watcher.UpdateWatchedSensors();
				
				var cpuTemperature = (int)Math.Round((double) cpuTempSensor.Value);
				var gpuTemperature = (int)Math.Round((double) gpuTempSensor.Value);
				
				var cpuUsage = (int)Math.Round((double) cpuUsageSensor.Value);
				var gpuUsage = (int)Math.Round((double)gpuUsageSensor.Value);
				
				var ramUsage = (int)Math.Round((double) ramUsageSensor.Value);
				var vramUsage = (int)Math.Round((double) vramUsageSensor.Value);

				SetDialValues(1, cpuTemperature, 35);
				SetDialValues(2, gpuTemperature, 35);
				
				SetDialValues(3, cpuUsage);
				SetDialValues(4, gpuUsage);
				
				SetDialValues(5, ramUsage);
				SetDialValues(6, vramUsage);

				await Task.Delay(1000);
			}
			
			watcher.Dispose();
		}

		private void SetDialLabel(int dial, string label)
		{
			SendCommand($"dial{dial}l.txt=\"{label}\"");
		}

		private void SetDialValues(int dial, int value, int minValue = 0, int maxValue = 100)
		{
			SendCommand(
				$"dial{dial}.pic={GetProgressBarPicId(value, minValue, maxValue)}",
				$"dial{dial}v.val={value}",
				$"dial{dial}l.xcen=1");
		}

		private static int GetProgressBarPicId(int value, int minValue = 0, int maxValue = 100)
		{
			value = Math.Clamp(value, minValue, maxValue);
			var valuePercentage = ((double)value - minValue) / (maxValue - minValue) * 100;

			return (int)Math.Floor(valuePercentage / 5) + ProgressBarStartIndex;
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
			}
		}

		private static async Task<SerialPort> GetSerialPort(string portName, int initialBaudRate = 9600, int newBaudRate = 57600)
		{
			var port = new SerialPort(portName, initialBaudRate);

			port.Open();

			while (!port.IsOpen)
				await Task.Delay(10);

			SendCommand(port, string.Empty);

			if (newBaudRate == initialBaudRate)
				return port;

			SendCommand(port, $"baud={newBaudRate}");

			await Task.Delay(500);

			port.Close();
			port.Dispose();

			port = new SerialPort(portName, newBaudRate);
			port.Open();

			await Task.Delay(500);

			while (!port.IsOpen)
				await Task.Delay(10);

			SendCommand(port, string.Empty);

			return port;
		}

		private static readonly byte[] InterCommand = { 0xFF, 0xFF, 0xFF };
		
		private void SendCommand(params string[] commands)
		{
			SendCommand(_port, commands);
		}
		
		private static void SendCommand(SerialPort port, params string[] commands)
		{
			using var commandBytes = new MemoryStream();

			foreach (var command in commands)
			{
				commandBytes.Write(Encoding.UTF8.GetBytes(command));
				commandBytes.Write(InterCommand);
			}

			var buffer = commandBytes.ToArray();
			//port.Write(buffer, 0, buffer.Length);
			port.BaseStream.Write(buffer, 0, buffer.Length);
			port.BaseStream.Flush();

			System.Console.WriteLine($"Write: {Encoding.UTF8.GetString(commandBytes.ToArray())}");
		}
	}
}

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using AssemblyRebuilder.Properties;

namespace AssemblyRebuilder {
	internal static class Program {
		private struct SectionHeader {
			public uint VirtualSize;

			public uint VirtualAddress;

			public uint RawSize;

			public uint RawAddress;

			public SectionHeader(uint virtualSize, uint virtualAddress, uint rawSize, uint rawAddress) {
				VirtualSize = virtualSize;
				VirtualAddress = virtualAddress;
				RawSize = rawSize;
				RawAddress = rawAddress;
			}
		}

		private static void Main(string[] args) {
			if (args == null || args.Length != 1)
				return;

			string originalAssemblyPath;
			bool isAssembly;
			string clrVersion;
			bool is64Bit;
			bool isClr4x;
			string assemblyName;
			string assemblyPath;
			string ilPath;
			string resourcePath;
			string rebuiltAssemblyPath;
			StringBuilder buffer;

			Console.Title = ConsoleTitleUtils.GetTitle();
			originalAssemblyPath = Path.GetFullPath(args[0]);
			if (!File.Exists(originalAssemblyPath)) {
				Console.WriteLine("File doesn't exist.");
				return;
			}
			ReadAssemblyInfo(originalAssemblyPath, out isAssembly, out clrVersion, out is64Bit);
			if (!isAssembly) {
				Console.WriteLine("File isn't a valid .NET assembly.");
				return;
			}
			assemblyName = Path.GetFileName(originalAssemblyPath);
			assemblyPath = Path.Combine(Path.GetDirectoryName(originalAssemblyPath), assemblyName + "_il", assemblyName);
			Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath));
			File.Copy(originalAssemblyPath, assemblyPath, true);
			Console.WriteLine("ClrVersion: " + clrVersion);
			Console.WriteLine("Is64Bit: " + is64Bit.ToString());
			Console.WriteLine();
			isClr4x = clrVersion.StartsWith("v4");
			ilPath = Path.ChangeExtension(assemblyPath, ".il");
			Console.WriteLine("Disassembling...");
			CallProcess(Settings.Default.ILDasmPath, string.Format("\"{0}\" /out=\"{1}\" {2}", assemblyPath, ilPath, Settings.Default.ILDasmOptions));
			File.WriteAllText(ilPath, File.ReadAllText(ilPath, Encoding.Default), new UTF8Encoding(true));
			resourcePath = Path.ChangeExtension(assemblyPath, ".res");
			rebuiltAssemblyPath = PathInsertPostfix(assemblyPath, ".rb");
			buffer = new StringBuilder();
			buffer.AppendFormat("\"{0}\"", ilPath);
			buffer.Append(assemblyPath.EndsWith(".exe") ? " /exe" : " /dll");
			buffer.AppendFormat(" /output=\"{0}\"", rebuiltAssemblyPath);
			if (File.Exists(resourcePath))
				buffer.AppendFormat(" /resource=\"{0}\"", resourcePath);
			buffer.Append(is64Bit ? Settings.Default.ILAsmOptions64 : Settings.Default.ILAsmOptions);
			Console.WriteLine("Saving: " + rebuiltAssemblyPath);
			CallProcess(isClr4x ? Settings.Default.ILAsm4xPath : Settings.Default.ILAsm2xPath, buffer.ToString());
			File.Copy(rebuiltAssemblyPath, Path.Combine(Path.GetDirectoryName(originalAssemblyPath), Path.GetFileName(rebuiltAssemblyPath)), true);
			Console.WriteLine("Finished.");
			Console.ReadKey(true);
		}

		private static void ReadAssemblyInfo(string assemblyPath, out bool isAssembly, out string clrVersion, out bool is64Bit) {
			try {
				isAssembly = true;
				using (BinaryReader reader = new BinaryReader(new FileStream(assemblyPath, FileMode.Open, FileAccess.Read))) {
					clrVersion = ReadVersionString(reader);
					ReadPEInfo(reader, out _, out is64Bit);
				}
			}
			catch {
				isAssembly = false;
				clrVersion = null;
				is64Bit = false;
			}
		}

		private static string ReadVersionString(BinaryReader reader) {
			uint peOffset;
			bool is64Bit;
			SectionHeader[] sectionHeaders;
			uint rva;
			SectionHeader sectionHeader;

			ReadPEInfo(reader, out peOffset, out is64Bit);
			reader.BaseStream.Position = peOffset + (is64Bit ? 0xF8 : 0xE8);
			rva = reader.ReadUInt32();
			// .Net Metadata Directory RVA
			if (rva == 0)
				throw new BadImageFormatException("File isn't a valid .NET assembly.");
			sectionHeaders = GetSectionHeaders(reader);
			sectionHeader = GetSectionHeader(rva, sectionHeaders);
			reader.BaseStream.Position = sectionHeader.RawAddress + rva - sectionHeader.VirtualAddress + 0x8;
			// .Net Metadata Directory FileOffset
			rva = reader.ReadUInt32();
			// .Net Metadata RVA
			if (rva == 0)
				throw new BadImageFormatException("File isn't a valid .NET assembly.");
			sectionHeader = GetSectionHeader(rva, sectionHeaders);
			reader.BaseStream.Position = sectionHeader.RawAddress + rva - sectionHeader.VirtualAddress + 0xC;
			// .Net Metadata FileOffset
			return Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32() - 2));
		}

		private static void ReadPEInfo(BinaryReader reader, out uint peOffset, out bool is64Bit) {
			ushort machine;

			reader.BaseStream.Position = 0x3C;
			peOffset = reader.ReadUInt32();
			reader.BaseStream.Position = peOffset + 0x4;
			machine = reader.ReadUInt16();
			if (machine != 0x14C && machine != 0x8664)
				throw new BadImageFormatException("Invalid \"Machine\" in FileHeader.");
			is64Bit = machine == 0x8664;
		}

		private static SectionHeader[] GetSectionHeaders(BinaryReader reader) {
			uint ntHeaderOffset;
			bool is64Bit;
			ushort numberOfSections;
			SectionHeader[] sectionHeaders;

			ReadPEInfo(reader, out ntHeaderOffset, out is64Bit);
			numberOfSections = reader.ReadUInt16();
			reader.BaseStream.Position = ntHeaderOffset + (is64Bit ? 0x108 : 0xF8);
			sectionHeaders = new SectionHeader[numberOfSections];
			for (int i = 0; i < numberOfSections; i++) {
				reader.BaseStream.Position += 0x8;
				sectionHeaders[i] = new SectionHeader(reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32());
				reader.BaseStream.Position += 0x10;
			}
			return sectionHeaders;
		}

		private static SectionHeader GetSectionHeader(uint rva, SectionHeader[] sectionHeaders) {
			foreach (SectionHeader sectionHeader in sectionHeaders)
				if (rva >= sectionHeader.VirtualAddress && rva < sectionHeader.VirtualAddress + Math.Max(sectionHeader.VirtualSize, sectionHeader.RawSize))
					return sectionHeader;
			throw new BadImageFormatException("Can't get section from specific RVA.");
		}

		private static void CallProcess(string filePath, string arguments) {
			Console.WriteLine();
			Console.WriteLine($"\"{filePath}\" {arguments}");
			Console.WriteLine();
			using (Process process = new Process() {
				StartInfo = new ProcessStartInfo(filePath, arguments) {
					CreateNoWindow = false,
					UseShellExecute = false
				}
			}) {
				process.Start();
				process.WaitForExit();
			}
		}

		private static string PathInsertPostfix(string path, string postfix) {
			return Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + postfix + Path.GetExtension(path));
		}
	}
}

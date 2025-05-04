using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace RP.Tcp.Communication.Message.Validation
{
	public static class Validation
	{
		public static byte[] Start = new byte[] { 10, 20, 30, 40, 50 };
		public static byte[] End = new byte[] { 80, 90, 100 };
		public static byte[] Count = new byte[] { 15, 25, 35, 45, 55 };

		public static string ValidationFailedExceptionMessage = "Failed reading data by validation test!";

		public static void Validate(BinaryReader br, byte[] arr)
		{
			var count = arr.Length;
			for (int i = 0; i < count; i++)
				if (arr[i] != br.ReadByte())
					throw new Exception(ValidationFailedExceptionMessage);
		}

		public static bool Sync(BinaryReader br, byte[] arr)
		{
			var readArr = new char[arr.Length];
			int i = 0;

			while (true)
			{
				var readChar = br.ReadByte();
				if (readChar == arr[i])
				{
					i++;
				}
				else
				{
					if (readChar == arr[0])
						i = 1;
					else
						i = 0;
				}

				if (i == arr.Length)
					return true;
			}
		}

		public static void Validate(Stream br, byte[] arr)
		{
			var count = arr.Length;
			for (int i = 0; i < count; i++)
			{
				int readByte;
				do
				{
					readByte = br.ReadByte();
				}
				while (readByte < 0);

				if (arr[i] != readByte)
					throw new Exception(ValidationFailedExceptionMessage);
			}
		}

		public static bool Sync(Stream br, byte[] arr)
		{
			var readArr = new char[arr.Length];
			int i = 0;

			while (true)
			{
				int readByte;
				do
				{
					readByte = br.ReadByte();
				}
				while (readByte < 0);

				if (readByte == arr[i])
				{
					i++;
				}
				else
				{
					if (readByte == arr[0])
						i = 1;
					else
						i = 0;
				}

				if (i == arr.Length)
					return true;
			}
		}

		public static void MaxCount(int count, int maxCount)
		{
			if (count > maxCount)
				throw new Exception(ValidationFailedExceptionMessage);
		}
	}

}
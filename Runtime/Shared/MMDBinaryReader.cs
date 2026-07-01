


using System;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace UMT
{
    /// <summary>
    /// Shared little-endian binary reader for MMD formats (PMX/VMD), providing ASCII, CP932, PMX-text, primitive, vector/quaternion, and variable-width index reads over a stream.
    /// </summary>
    public sealed class MMDBinaryReader : IDisposable
    {
        private readonly BinaryReader m_Reader;
        private readonly bool m_LeaveOpen;

        /// <summary>
        /// Creates a reader over the given stream.
        /// </summary>
        /// <param name="stream">The source stream to read MMD data from.</param>
        /// <param name="leaveOpen">When <c>true</c>, the underlying stream is left open on <see cref="Dispose"/>.</param>
        public MMDBinaryReader(Stream stream, bool leaveOpen = false)
        {
            m_Reader = new BinaryReader(stream);
            m_LeaveOpen = leaveOpen;
        }

        /// <summary>
        /// Gets the current byte position within the underlying stream.
        /// </summary>
        public long Position => m_Reader.BaseStream.Position;

        /// <summary>
        /// Reads a raw byte array of the requested length.
        /// </summary>
        /// <param name="count">The number of bytes to read.</param>
        /// <returns>The bytes read from the stream.</returns>
        public byte[] ReadBytes(int count)
        {
            return m_Reader.ReadBytes(count);
        }

        /// <summary>
        /// Reads an unsigned 8-bit integer.
        /// </summary>
        /// <returns>The byte value read.</returns>
        public byte ReadU8()
        {
            return m_Reader.ReadByte();
        }

        /// <summary>
        /// Reads a signed 8-bit integer.
        /// </summary>
        /// <returns>The signed byte value read.</returns>
        public sbyte ReadI8()
        {
            return m_Reader.ReadSByte();
        }

        /// <summary>
        /// Reads an unsigned 16-bit integer.
        /// </summary>
        /// <returns>The 16-bit unsigned value read.</returns>
        public ushort ReadU16()
        {
            return m_Reader.ReadUInt16();
        }

        /// <summary>
        /// Reads a signed 16-bit integer.
        /// </summary>
        /// <returns>The 16-bit signed value read.</returns>
        public short ReadI16()
        {
            return m_Reader.ReadInt16();
        }

        /// <summary>
        /// Reads an unsigned 32-bit integer.
        /// </summary>
        /// <returns>The 32-bit unsigned value read.</returns>
        public uint ReadU32()
        {
            return m_Reader.ReadUInt32();
        }

        /// <summary>
        /// Reads a signed 32-bit integer.
        /// </summary>
        /// <returns>The 32-bit signed value read.</returns>
        public int ReadI32()
        {
            return m_Reader.ReadInt32();
        }

        /// <summary>
        /// Reads a 32-bit IEEE single-precision float.
        /// </summary>
        /// <returns>The float value read.</returns>
        public float ReadF32()
        {
            return m_Reader.ReadSingle();
        }

        /// <summary>
        /// Reads a fixed-length ASCII string into a 32-byte fixed string, trimming trailing null characters.
        /// </summary>
        /// <param name="byteCount">The number of bytes to read and decode as ASCII.</param>
        /// <returns>The decoded fixed string.</returns>
        public FixedString32Bytes ReadAscii32Bytes(int byteCount)
        {
            FixedString32Bytes result = default;
            result.CopyFromTruncated(Encoding.ASCII.GetString(m_Reader.ReadBytes(byteCount), 0, byteCount).TrimEnd('\0'));
            return result;
        }

        /// <summary>
        /// Reads a fixed-length ASCII string into a 128-byte fixed string, trimming trailing null characters.
        /// </summary>
        /// <param name="byteCount">The number of bytes to read and decode as ASCII.</param>
        /// <returns>The decoded fixed string.</returns>
        public FixedString128Bytes ReadAscii128Bytes(int byteCount)
        {
            FixedString128Bytes result = default;
            result.CopyFromTruncated(Encoding.ASCII.GetString(m_Reader.ReadBytes(byteCount), 0, byteCount).TrimEnd('\0'));
            return result;
        }

        /// <summary>
        /// Reads a fixed-length CP932 (Shift-JIS) string into a 32-byte fixed string, trimming trailing null characters.
        /// </summary>
        /// <param name="byteCount">The number of bytes to read and decode as CP932.</param>
        /// <returns>The decoded fixed string.</returns>
        public FixedString32Bytes ReadCP932(int byteCount)
        {
            // Uses the self-contained ShiftJisDecoder rather than Encoding.GetEncoding(932), which is unavailable on IL2CPP/WebGL (throws NotSupportedException).
            FixedString32Bytes result = default;
            result.CopyFromTruncated(ShiftJisDecoder.GetString(m_Reader.ReadBytes(byteCount), 0, byteCount).TrimEnd('\0'));
            return result;
        }

        /// <summary>
        /// Reads two consecutive 32-bit floats as a 2-component vector.
        /// </summary>
        /// <returns>The vector read.</returns>
        public float2 ReadVec2()
        {
            return new float2(ReadF32(), ReadF32());
        }

        /// <summary>
        /// Reads three consecutive 32-bit floats as a 3-component vector.
        /// </summary>
        /// <returns>The vector read.</returns>
        public float3 ReadVec3()
        {
            return new float3(ReadF32(), ReadF32(), ReadF32());
        }

        /// <summary>
        /// Reads four consecutive 32-bit floats as a 4-component vector.
        /// </summary>
        /// <returns>The vector read.</returns>
        public float4 ReadVec4()
        {
            return new float4(ReadF32(), ReadF32(), ReadF32(), ReadF32());
        }

        /// <summary>
        /// Reads four consecutive 32-bit floats as a quaternion in (x, y, z, w) order.
        /// </summary>
        /// <returns>The quaternion read.</returns>
        public quaternion ReadQuaternion()
        {
            return new quaternion(ReadF32(), ReadF32(), ReadF32(), ReadF32());
        }

        /// <summary>
        /// Reads four consecutive 32-bit floats as an RGBA color.
        /// </summary>
        /// <returns>The color read.</returns>
        public Color ReadColor4()
        {
            return new Color(ReadF32(), ReadF32(), ReadF32(), ReadF32());
        }

        /// <summary>
        /// Reads a length-prefixed PMX text field, decoding it as UTF-16LE or UTF-8 based on the header encoding.
        /// </summary>
        /// <param name="textEncoding">The PMX text encoding declared in the header.</param>
        /// <returns>The decoded text, or the default empty value when the length is zero.</returns>
        /// <exception cref="InvalidDataException">Thrown when the encoded byte length is negative.</exception>
        public FixedString128Bytes ReadPMXText(PMXHeader.TextEncoding textEncoding)
        {
            int byteLength = ReadI32();
            if (byteLength < 0)
            {
                throw new InvalidDataException($"Invalid negative PMX text byte length {byteLength} at {Position}.");
            }

            if (byteLength == 0)
            {
                return default;
            }

            byte[] bytes = m_Reader.ReadBytes(byteLength);
            FixedString128Bytes result = default;
            string text = textEncoding == PMXHeader.TextEncoding.UTF16LE ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes);
            result.CopyFromTruncated(text);
            return result;
        }

        /// <summary>
        /// Reads a signed PMX index whose width (1, 2, or 4 bytes) is determined by the given size.
        /// </summary>
        /// <param name="size">The index byte width: 1, 2, or 4.</param>
        /// <returns>The signed index value read.</returns>
        /// <exception cref="InvalidDataException">Thrown when <paramref name="size"/> is not 1, 2, or 4.</exception>
        public int ReadIndex(byte size)
        {
            switch (size)
            {
                case 1:
                    return ReadI8();
                case 2:
                    return ReadI16();
                case 4:
                    return ReadI32();
                default:
                    throw new InvalidDataException($"Invalid PMX index size {size}.");
            }
        }

        /// <summary>
        /// Reads an unsigned PMX vertex index whose width (1, 2, or 4 bytes) is determined by the given size.
        /// </summary>
        /// <param name="size">The index byte width: 1, 2, or 4.</param>
        /// <returns>The unsigned vertex index value read.</returns>
        /// <exception cref="InvalidDataException">Thrown when <paramref name="size"/> is not 1, 2, or 4.</exception>
        public uint ReadVertexIndex(byte size)
        {
            switch (size)
            {
                case 1:
                    return ReadU8();
                case 2:
                    return ReadU16();
                case 4:
                    return ReadU32();
                default:
                    throw new InvalidDataException($"Invalid PMX index size {size}.");
            }
        }

        /// <summary>
        /// Checks whether the stream has more data, validating that at least the requested number of bytes remains when any data is left.
        /// </summary>
        /// <param name="byteCount">The minimum number of bytes expected to remain if the stream is not at its end.</param>
        /// <returns><c>true</c> if data remains; <c>false</c> when the stream is exactly at its end.</returns>
        /// <exception cref="EndOfStreamException">Thrown when some data remains but fewer than <paramref name="byteCount"/> bytes are available.</exception>
        public bool HasRemaining(int byteCount)
        {
            long remaining = m_Reader.BaseStream.Length - m_Reader.BaseStream.Position;
            if (remaining == 0)
            {
                return false;
            }
            if (remaining < byteCount)
            {
                throw new EndOfStreamException($"File ended in the middle of a section. Expected {byteCount} more bytes but found {remaining}.");
            }
            return true;
        }

        /// <summary>
        /// Disposes the reader, also closing the underlying stream unless it was constructed with <c>leaveOpen</c> set.
        /// </summary>
        public void Dispose()
        {
            if (!m_LeaveOpen)
            {
                m_Reader.Dispose();
            }
        }
    }
}

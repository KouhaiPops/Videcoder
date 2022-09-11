using System;

namespace Videcoder
{
    public class DecoderException : Exception
    {
        public int Averror { get; }
        public DecoderException()
        {
        }
        public DecoderException(string message, int errorCode) : this($"AVERROR: {errorCode}{Environment.NewLine}Message: {message}")
        {
            Averror = errorCode;
        }

        public DecoderException(string message) : base(message) { }

        public DecoderException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}

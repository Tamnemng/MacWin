using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace MacWin.Services
{
    /// <summary>A soft, original start-up chord synthesised at runtime (no audio files shipped).</summary>
    public static class Chime
    {
        /// <summary>A start-up sound the user placed on their own PC replaces the built-in chord.</summary>
        public static string UserSoundPath => Path.Combine(AppSettings.Folder, "Sounds", "startup.wav");

        public static void Play()
        {
            Task.Run(() =>
            {
                try
                {
                    if (File.Exists(UserSoundPath))
                    {
                        using var custom = new SoundPlayer(UserSoundPath);
                        custom.PlaySync();
                        return;
                    }
                    using var ms = new MemoryStream(Synthesize());
                    using var player = new SoundPlayer(ms);
                    player.PlaySync();
                }
                catch { }
            });
        }

        static byte[] Synthesize()
        {
            const int rate = 44100;
            const double seconds = 2.6;
            int n = (int)(rate * seconds);
            // Bb major add9 voicing
            double[] freqs = { 116.54, 174.61, 233.08, 293.66, 349.23, 523.25 };
            double[] gains = { 0.30, 0.22, 0.20, 0.16, 0.12, 0.06 };
            var left = new double[n];
            var right = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate;
                double env = Math.Min(1.0, t / 0.025) * Math.Exp(-t * 1.55);
                double l = 0, r = 0;
                for (int k = 0; k < freqs.Length; k++)
                {
                    double f = freqs[k];
                    double tone = Math.Sin(2 * Math.PI * f * t) + 0.25 * Math.Sin(2 * Math.PI * f * 2 * t) * Math.Exp(-t * 3);
                    double detune = Math.Sin(2 * Math.PI * f * 1.003 * t);
                    l += gains[k] * (tone * 0.8 + detune * 0.2);
                    r += gains[k] * (tone * 0.8 + Math.Sin(2 * Math.PI * f * 0.997 * t) * 0.2);
                }
                left[i] = l * env;
                right[i] = r * env;
            }
            // gentle one-pole low-pass for warmth
            double a = 0.18, pl = 0, pr = 0;
            for (int i = 0; i < n; i++) { pl += a * (left[i] - pl); pr += a * (right[i] - pr); left[i] = pl; right[i] = pr; }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int dataLen = n * 4;
            w.Write("RIFF"u8.ToArray()); w.Write(36 + dataLen); w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8.ToArray()); w.Write(dataLen);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp(left[i] * 26000, short.MinValue, short.MaxValue));
                w.Write((short)Math.Clamp(right[i] * 26000, short.MinValue, short.MaxValue));
            }
            w.Flush();
            return ms.ToArray();
        }
    }
}

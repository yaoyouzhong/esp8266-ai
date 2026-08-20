namespace AIClockBridge;

static class CompletionChime
{
    const int SampleRate = 44_100;
    static readonly object PlaybackLock = new();
    static readonly byte[] Wave = BuildWave();

    public static void Play()
    {
        lock (PlaybackLock)
        {
            try
            {
                using var stream = new MemoryStream(Wave, writable: false);
                using var player = new System.Media.SoundPlayer(stream);
                player.PlaySync();
            }
            catch
            {
                // Keep a completion cue even if the synthesized WAV cannot be played.
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
    }

    static byte[] BuildWave()
    {
        // A short C-major arpeggio: warmer and more recognizable than a generic
        // Windows alert, while remaining brief enough for frequent completions.
        var notes = new[]
        {
            new Note(0.00, 0.24, 523.25, 0.46), // C5
            new Note(0.12, 0.28, 659.25, 0.42), // E5
            new Note(0.24, 0.38, 783.99, 0.38), // G5
            new Note(0.38, 0.44, 1046.50, 0.34), // C6
        };
        var sampleCount = (int)(SampleRate * 0.82);
        var pcm = new short[sampleCount];

        for (var i = 0; i < sampleCount; i++)
        {
            var time = (double)i / SampleRate;
            var mixed = 0.0;
            foreach (var note in notes)
            {
                var age = time - note.Start;
                if (age < 0 || age >= note.Duration) continue;

                var attack = Math.Min(1.0, age / 0.008);
                var release = Math.Min(1.0, (note.Duration - age) / 0.12);
                var decay = 1.0 - 0.22 * age / note.Duration;
                var phase = 2 * Math.PI * note.Frequency * age;
                var tone = Math.Sin(phase) + 0.14 * Math.Sin(phase * 2);
                mixed += tone * note.Gain * attack * release * decay;
            }

            mixed = Math.Clamp(mixed * 0.42, -1.0, 1.0);
            pcm[i] = (short)Math.Round(mixed * short.MaxValue);
        }

        using var output = new MemoryStream(44 + pcm.Length * sizeof(short));
        using var writer = new BinaryWriter(output);
        var dataSize = pcm.Length * sizeof(short);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        foreach (var sample in pcm) writer.Write(sample);
        writer.Flush();
        return output.ToArray();
    }

    readonly record struct Note(double Start, double Duration, double Frequency, double Gain);
}

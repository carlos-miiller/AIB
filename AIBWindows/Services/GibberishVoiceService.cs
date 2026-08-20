using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AIB.Services
{
    public static class GibberishVoiceService
    {
        private static WaveOutEvent? _waveOut;
        private static MixingSampleProvider? _mixer;
        private static Random _random = new Random();

        // Default settings
        private static double _baseFrequency = 500.0;
        private static bool _isInitialized = false;

        public static void Initialize()
        {
            if (_isInitialized) return;
            try
            {
                _waveOut = new WaveOutEvent();
                _waveOut.DesiredLatency = 50; 
                _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 1));
                _mixer.ReadFully = true;
                _waveOut.Init(_mixer);
                _waveOut.Play();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GibberishVoice] Error initializing audio: {ex.Message}");
            }
        }

        public static void SpeakChunk(string textChunk)
        {
            if (!_isInitialized || _mixer == null || string.IsNullOrWhiteSpace(textChunk)) return;

            string cleanChunk = textChunk.Trim();
            if (string.IsNullOrEmpty(cleanChunk)) return;
            if (cleanChunk == "." || cleanChunk == "," || cleanChunk == "!" || cleanChunk == "?") return;

            char c = cleanChunk[0];
            double pitchMod = (c % 10) * 15.0; // Variação de 0 a 150 Hz
            double frequency = _baseFrequency + pitchMod;

            if (textChunk.Contains("?")) frequency += 200;

            double durationSeconds = 0.05; 

            var generator = new SignalGenerator(44100, 1)
            {
                Type = SignalGeneratorType.Triangle, 
                Gain = 0.05, // Volume bem baixo
                Frequency = frequency
            };

            var sampleProvider = generator.Take(TimeSpan.FromSeconds(durationSeconds));
            _mixer.AddMixerInput(sampleProvider);
        }

        public static void Stop()
        {
            _mixer?.RemoveAllMixerInputs();
        }
    }
}

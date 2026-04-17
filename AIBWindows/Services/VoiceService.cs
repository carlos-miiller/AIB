using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace AIB.Services
{
    public class TranscriptionEventArgs : EventArgs
    {
        public string Text { get; set; }
        public bool IsFinal { get; set; }
    }

    public class VoiceService : IDisposable
    {
        private readonly string _modelPath;
        private WhisperFactory _factory;
        private WhisperProcessor _processor;
        private WaveInEvent _waveIn;
        private readonly MemoryStream _audioBuffer = new();
        private CancellationTokenSource _cts;
        
        public event EventHandler<TranscriptionEventArgs> OnTranscriptionUpdated;
        public event EventHandler<string> OnSpeechDetected;
        
        private bool _isListening;
        private DateTime _lastSpeechTime = DateTime.MinValue;
        private const int SILENCE_THRESHOLD_MS = 1200;
        private bool _isProcessing;
        
        // Wake word
        private const string WAKE_WORD = "AIB";

        public VoiceService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var modelDir = Path.Combine(appData, "AIB", "Models");
            Directory.CreateDirectory(modelDir);
            _modelPath = Path.Combine(modelDir, "ggml-base.bin");
        }

        public async Task InitializeAsync()
        {
            if (!File.Exists(_modelPath))
            {
                await DownloadModelAsync();
            }

            _factory = WhisperFactory.FromPath(_modelPath);
            _processor = _factory.CreateBuilder()
                .WithLanguage("pt") // Force Portuguese for better accuracy as discussed
                .Build();
        }

        private async Task DownloadModelAsync()
        {
            using var client = new HttpClient();
            // Using base model for a good balance between speed and accuracy
            var url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
            var response = await client.GetAsync(url);
            using var fs = new FileStream(_modelPath, FileMode.Create);
            await response.Content.CopyToAsync(fs);
        }

        public void StartListening()
        {
            if (_isListening) return;

            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1) // Whisper requirement: 16kHz, 16-bit, Mono
            };

            _waveIn.DataAvailable += (s, e) =>
            {
                _audioBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            };

            _cts = new CancellationTokenSource();
            _isListening = true;
            _waveIn.StartRecording();

            // Background processing loop
            Task.Run(() => ProcessingLoop(_cts.Token));
        }

        public void StopListening()
        {
            _isListening = false;
            _cts?.Cancel();
            _waveIn?.StopRecording();
            _waveIn?.Dispose();
            _waveIn = null;
        }

        private async Task ProcessingLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(500, token); // Check every 500ms

                if (_audioBuffer.Length > 32000) // Minimum ~1sec of audio (16k * 2 bytes/sample)
                {
                    await ProcessCurrentBuffer();
                }
            }
        }

        private async Task ProcessCurrentBuffer()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                byte[] audioData;
                lock (_audioBuffer)
                {
                    audioData = _audioBuffer.ToArray();
                }

                using var ms = new MemoryStream(audioData);
                var segments = _processor.ProcessAsync(ms);

                string fullText = "";
                await foreach (var segment in segments)
                {
                    fullText += segment.Text;
                }

                if (!string.IsNullOrWhiteSpace(fullText))
                {
                    _lastSpeechTime = DateTime.Now;
                    
                    // Simple "AIB" detection logic
                    bool hasWakeWord = fullText.Contains(WAKE_WORD, StringComparison.OrdinalIgnoreCase);
                    
                    OnTranscriptionUpdated?.Invoke(this, new TranscriptionEventArgs 
                    { 
                        Text = fullText.Trim(),
                        IsFinal = false 
                    });

                    // Auto-commit logic
                    if (DateTime.Now - _lastSpeechTime > TimeSpan.FromMilliseconds(SILENCE_THRESHOLD_MS))
                    {
                        // If it contains the wake word or if we are already in a "session"
                        if (hasWakeWord)
                        {
                            OnTranscriptionUpdated?.Invoke(this, new TranscriptionEventArgs 
                            { 
                                Text = fullText.Trim(),
                                IsFinal = true 
                            });
                            ClearBuffer();
                        }
                    }
                }
                else
                {
                    // Check for silence timeout to clear buffer if no speech detected
                    if (DateTime.Now - _lastSpeechTime > TimeSpan.FromSeconds(3) && _audioBuffer.Length > 0)
                    {
                        ClearBuffer();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro no processamento de voz: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private void ClearBuffer()
        {
            lock (_audioBuffer)
            {
                _audioBuffer.SetLength(0);
                _audioBuffer.Position = 0;
            }
        }

        public void Dispose()
        {
            StopListening();
            _processor?.Dispose();
            _factory?.Dispose();
            _audioBuffer?.Dispose();
        }
    }
}

using System;
using System.Threading.Tasks;
namespace AIB.Services {
    public class TranscriptionEventArgs : EventArgs {
        public string Text { get; set; }
        public bool IsFinal { get; set; }
    }
    public class VoiceService {
        public event EventHandler<TranscriptionEventArgs> OnTranscriptionUpdated;
        public Task InitializeAsync() => Task.CompletedTask;
        public void StartListening() { }
        public void StopListening() { }
        public void Dispose() { }
    }
}

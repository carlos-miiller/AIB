using System;
using System.Threading.Tasks;
namespace AIB.Services {
    public class TranscriptionEventArgs : EventArgs {
        public string Text { get; set; } = "";
        public bool IsFinal { get; set; }
    }
    public class VoiceService {
        // Estes eventos TEM assinantes (a ChatWindow escuta os dois), mas ainda nao ha
        // quem os dispare: o servico e um esqueleto. E disso que o CS0067 reclama — ele diz
        // "nunca disparado", nao "nunca assinado", e le-lo ao contrario custou um build
        // quebrado. O aviso fica silenciado AQUI, nominalmente, para nao mascarar um evento
        // morto de verdade em outro lugar do projeto.
#pragma warning disable CS0067
        public event EventHandler<TranscriptionEventArgs>? OnTranscriptionUpdated;
#pragma warning restore CS0067

        public Task InitializeAsync() => Task.CompletedTask;
        public void StartListening() { }
        public void StopListening() { }
        public void Dispose() { }
    }
}

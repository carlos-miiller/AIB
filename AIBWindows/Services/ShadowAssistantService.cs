using System;
namespace AIB.Services {
    public class ShadowAssistantService {
        // Estes eventos TEM assinantes (a ChatWindow escuta os dois), mas ainda nao ha
        // quem os dispare: o servico e um esqueleto. E disso que o CS0067 reclama — ele diz
        // "nunca disparado", nao "nunca assinado", e le-lo ao contrario custou um build
        // quebrado. O aviso fica silenciado AQUI, nominalmente, para nao mascarar um evento
        // morto de verdade em outro lugar do projeto.
#pragma warning disable CS0067
        public event Action<string>? OnSuggestionReceived;
        public event Action<int>? OnActiveScreenChanged;
#pragma warning restore CS0067
        public ShadowAssistantService(ConversationService conversation, SettingsService settings) { }
        public void RegisterOwnWindow(IntPtr handle) { }
        public void Start() { }
        public void Stop() { }
        public static int GetCurrentScreenIndex() => 0;
    }
}

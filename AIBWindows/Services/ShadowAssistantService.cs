using System;
namespace AIB.Services {
    public class ShadowAssistantService {
        public event Action<string> OnSuggestionReceived;
        public event Action<int> OnActiveScreenChanged;
        public ShadowAssistantService(OpenAIService openAI, SettingsService settings) { }
        public void RegisterOwnWindow(IntPtr handle) { }
        public void Start() { }
        public void Stop() { }
        public static int GetCurrentScreenIndex() => 0;
    }
}

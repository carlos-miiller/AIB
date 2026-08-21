using System;
using System.Net.Http;
using AIB.Services;
using AIB.Services.Ai;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    public class ChatProviderFactoryTests
    {
        private static UserAppSettings OpenAiSettings(string apiKey) => new UserAppSettings
        {
            AiProvider = "OpenAI",
            ModelName = "gpt-4o-mini",
            ApiUrl = "https://api.exemplo.test/v1",
            ApiKey = apiKey
        };

        private static ChatProviderFactory Build()
            => new ChatProviderFactory(new HttpClient(), new RegexToolCallHealer());

        [Fact]
        public void MesmasSettings_ReaproveitamOProvider()
        {
            var factory = Build();

            var first = factory.GetProvider(OpenAiSettings("chave-1"));
            var second = factory.GetProvider(OpenAiSettings("chave-1"));

            second.Should().BeSameAs(first);
        }

        [Fact]
        public void TrocaDeCredencial_ReconstroiOProvider()
        {
            // Defeito antigo: a chave de cache não continha a credencial, então quem trocava
            // a chave da API continuava tomando 401 com o cliente morto até reiniciar o app.
            var factory = Build();

            var first = factory.GetProvider(OpenAiSettings("chave-1"));
            var second = factory.GetProvider(OpenAiSettings("chave-2"));

            second.Should().NotBeSameAs(first);
        }

        [Fact]
        public void TrocaDeModelo_ReconstroiOProvider()
        {
            var factory = Build();

            var first = factory.GetProvider(OpenAiSettings("chave-1"));

            var other = OpenAiSettings("chave-1");
            other.ModelName = "gpt-4o";
            var second = factory.GetProvider(other);

            second.Should().NotBeSameAs(first);
        }

        [Fact]
        public void ProviderOllama_NaoConsultaOCofreEReaproveitaAInstancia()
        {
            var factory = Build();

            var settings = new UserAppSettings
            {
                AiProvider = "Ollama",
                ModelName = "modelo-de-teste",
                ApiUrl = "http://localhost:11434/v1",
                ApiKey = "use-vault"
            };

            var first = factory.GetProvider(settings);
            var second = factory.GetProvider(settings);

            first.Name.Should().Be("Ollama");
            second.Should().BeSameAs(first);
        }
    }
}

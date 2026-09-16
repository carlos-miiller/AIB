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
        private static UserAppSettings OpenRouter(string modelo = "deepseek/deepseek-chat") => new UserAppSettings
        {
            AiProvider = ProvedoresDeIa.OpenRouter,
            ModelName = modelo,
            ApiUrl = ProvedoresDeIa.UrlDoOpenRouter
        };

        /// <summary>A chave vem daqui, e não do cofre real do usuário.</summary>
        private static ChatProviderFactory Build(Func<string> chave)
            => new ChatProviderFactory(new HttpClient(), new RegexToolCallHealer(), _ => chave());

        [Fact]
        public void MesmasSettings_ReaproveitamOProvider()
        {
            var factory = Build(() => "sk-or-1");

            factory.GetProvider(OpenRouter()).Should().BeSameAs(factory.GetProvider(OpenRouter()));
        }

        [Fact]
        public void TrocaDeCredencial_ReconstroiOProvider()
        {
            // Defeito antigo: a chave de cache não continha a credencial, então quem trocava
            // a chave da API continuava tomando 401 com o cliente morto até reiniciar o app.
            string chave = "sk-or-1";
            var factory = Build(() => chave);

            var first = factory.GetProvider(OpenRouter());
            chave = "sk-or-2";
            var second = factory.GetProvider(OpenRouter());

            second.Should().NotBeSameAs(first);
        }

        [Fact]
        public void TrocaDeModelo_ReconstroiOProvider()
        {
            var factory = Build(() => "sk-or-1");

            factory.GetProvider(OpenRouter("a/b")).Should().NotBeSameAs(factory.GetProvider(OpenRouter("c/d")));
        }

        [Fact]
        public void TrocaDeProvedorFixo_ReconstroiOProvider()
        {
            // O provedor preferido vai no corpo de cada requisição; um provider em cache com o
            // valor velho continuaria roteando para o lugar antigo até reiniciar o app.
            var factory = Build(() => "sk-or-1");
            var livre = factory.GetProvider(OpenRouter());

            var fixo = OpenRouter();
            fixo.OpenRouterProvedorFixo = "DeepInfra";

            factory.GetProvider(fixo).Should().NotBeSameAs(livre);
            factory.GetProvider(fixo).Should().BeSameAs(factory.GetProvider(fixo));
        }

        [Fact]
        public void OpenRouter_VaiPeloProviderProprio()
        {
            Build(() => "sk-or-1").GetProvider(OpenRouter()).Should().BeOfType<OpenRouterProvider>();
        }

        [Fact]
        public void ConversaETriagem_EmProvedoresDiferentes_NaoSeDerrubamNoCache()
        {
            // A conversa no OpenRouter e a triagem no Ollama se alternam o tempo todo; com uma
            // instância só em cache, cada alternância construía um provider novo.
            var factory = Build(() => "sk-or-1");
            var ollama = new UserAppSettings { AiProvider = ProvedoresDeIa.Ollama, ModelName = "qwen" };

            var conversa = factory.GetProvider(OpenRouter());
            factory.GetProvider(ollama);

            factory.GetProvider(OpenRouter()).Should().BeSameAs(conversa);
        }

        [Fact]
        public void ProviderOllama_NaoConsultaOCofreEReaproveitaAInstancia()
        {
            int leituras = 0;
            var factory = new ChatProviderFactory(new HttpClient(), new RegexToolCallHealer(),
                p => { if (ProvedoresDeIa.SistemaDaChave(p) != null) leituras++; return ""; });

            var settings = new UserAppSettings
            {
                AiProvider = "Ollama",
                ModelName = "modelo-de-teste",
                ApiUrl = "http://localhost:11434/v1"
            };

            var first = factory.GetProvider(settings);
            var second = factory.GetProvider(settings);

            first.Name.Should().Be("Ollama");
            second.Should().BeSameAs(first);
            leituras.Should().Be(0);
        }
    }
}

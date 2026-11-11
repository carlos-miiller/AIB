using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIB.Services.Ai;
using FluentAssertions;
using Xunit;

namespace AIB.Tests
{
    /// <summary>
    /// A lista de modelos do Google vem da chave, e não mais de uma lista fixa. Caso real
    /// (23/10/2026): a lista fixa oferecia "gemini-2.5-pro", o Google respondeu 404 ("O Google não
    /// encontrou o modelo") e o usuário perguntou "não é possível obter dinamicamente os modelos?".
    /// </summary>
    public class CatalogoDoGoogleTests
    {
        private const string Pagina = """
        {"models":[
          {"name":"models/gemini-flash-latest","supportedGenerationMethods":["generateContent","countTokens"]},
          {"name":"models/gemini-3.1-pro-preview","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3.8-flash","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},
          {"name":"models/gemini-2.5-flash-preview-tts","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemini-3-pro-image","supportedGenerationMethods":["generateContent"]},
          {"name":"models/gemma-3-27b-it","supportedGenerationMethods":["generateContent"]},
          {"name":"models/imagen-4.0-generate-001","supportedGenerationMethods":["predict"]}
        ],"nextPageToken":"abc"}
        """;

        [Fact]
        public void Ler_FicaSoComOsGeminiDeConversa_SemOPrefixoModels()
        {
            var nomes = CatalogoDoGoogle.Ler(Pagina, out string? proxima);

            nomes.Should().BeEquivalentTo("gemini-flash-latest", "gemini-3.1-pro-preview", "gemini-3.8-flash");
            proxima.Should().Be("abc");
        }

        [Fact]
        public void Ordenar_PoeOsLatestPrimeiro_EOsMaisNovosEmCima()
        {
            CatalogoDoGoogle.Ordenar(new[] { "gemini-3.1-pro-preview", "gemini-flash-latest", "gemini-3.8-flash", "gemini-3.1-pro-preview" })
                .Should().Equal("gemini-flash-latest", "gemini-3.8-flash", "gemini-3.1-pro-preview");
        }

        [Fact]
        public async Task SemChave_NaoConsulta_EDevolveVazio()
        {
            // Vazio faz a tela ficar com a lista fixa; a rede nem é tocada.
            (await CatalogoDoGoogle.ListarAsync(new HttpClient(), "")).Should().BeEmpty();
        }
    }
}

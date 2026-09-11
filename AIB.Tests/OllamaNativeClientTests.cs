using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Moq.Protected;
using Xunit;
using FluentAssertions;
using AIB.Services;
using OpenAI.Chat;

namespace AIB.Tests
{
    public class OllamaNativeClientTests
    {
        [Fact]
        public async Task CompleteChatAsync_ShouldSendSameJsonPropertiesAsStreamChatAsync()
        {
            // Arrange
            var handlerMock = new Mock<HttpMessageHandler>();

            string? streamChatRequestBody = null;
            string? completeChatRequestBody = null;

            handlerMock
                .Protected()
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                .ReturnsAsync((HttpRequestMessage request, CancellationToken token) =>
                {
                    // O callback do ReturnsAsync é síncrono por contrato: ele devolve a
                    // RESPOSTA, não uma Task. Não há onde pôr um await, e o conteúdo aqui é
                    // uma StringContent em memória — não há E/S para bloquear.
#pragma warning disable xUnit1031
                    var content = request.Content?.ReadAsStringAsync(token).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                    
                    if (content != null && content.Contains("\"stream\":true"))
                    {
                        streamChatRequestBody = content;
                        return new HttpResponseMessage
                        {
                            StatusCode = HttpStatusCode.OK,
                            Content = new StringContent("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"done\":true}")
                        };
                    }
                    else
                    {
                        completeChatRequestBody = content;
                        return new HttpResponseMessage
                        {
                            StatusCode = HttpStatusCode.OK,
                            Content = new StringContent("{\"model\":\"llama3\",\"message\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"done\":true}")
                        };
                    }
                });

            var httpClient = new HttpClient(handlerMock.Object);
            var client = new OllamaNativeClient("http://localhost:11434/v1", httpClient);

            var history = new List<ChatMessage> { new UserChatMessage("Hi") };
            var tools = new List<ChatTool> { ChatTool.CreateFunctionTool("test", "test desc") };

            // Act
            // 1. Call StreamChatAsync
            var streamEnum = client.StreamChatAsync("llama3", history, tools, 0.7f, false, CancellationToken.None);
            await foreach (var item in streamEnum)
            {
                // Consume
            }

            // 2. Call CompleteChatAsync
            await client.CompleteChatAsync("llama3", history, tools, 0.7f, false, CancellationToken.None);

            // Assert
            streamChatRequestBody.Should().NotBeNull();
            completeChatRequestBody.Should().NotBeNull();

            var streamDoc = JsonDocument.Parse(streamChatRequestBody!);
            var completeDoc = JsonDocument.Parse(completeChatRequestBody!);

            var streamRoot = streamDoc.RootElement;
            var completeRoot = completeDoc.RootElement;

            // Assert exact same root properties
            var streamProperties = streamRoot.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
            var completeProperties = completeRoot.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();

            completeProperties.Should().BeEquivalentTo(streamProperties);

            // Assert options properties (like temperature, num_ctx)
            streamRoot.TryGetProperty("options", out var streamOptions).Should().BeTrue();
            completeRoot.TryGetProperty("options", out var completeOptions).Should().BeTrue();

            var streamOptionsProps = streamOptions.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();
            var completeOptionsProps = completeOptions.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToList();

            completeOptionsProps.Should().BeEquivalentTo(streamOptionsProps);

            // Assert tools property exists in both
            streamRoot.TryGetProperty("tools", out _).Should().BeTrue();
            completeRoot.TryGetProperty("tools", out _).Should().BeTrue();
        }
    }
}

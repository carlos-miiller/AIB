using System;
using System.Collections.Generic;
using System.Text.Json;
using AIB.Services.Ai;
using FluentAssertions;
using OpenAI.Chat;
using Xunit;

namespace AIB.Tests
{
    public class RegexToolCallHealerTests
    {
        private static ChatTool ReadFileTool() => ChatTool.CreateFunctionTool(
            functionName: "read_file",
            functionDescription: "Lê um arquivo.",
            functionParameters: BinaryData.FromString(
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}"));

        private static ChatTool CountTool() => ChatTool.CreateFunctionTool(
            functionName: "count_lines",
            functionDescription: "Conta linhas.",
            functionParameters: BinaryData.FromString(
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}},\"required\":[\"path\"]}"));

        private static ChatTool NoArgTool() => ChatTool.CreateFunctionTool(
            functionName: "ping",
            functionDescription: "Ping.",
            functionParameters: BinaryData.FromString("{\"type\":\"object\",\"properties\":{}}"));

        private static IReadOnlyList<ChatTool> Tools(params ChatTool[] tools) => tools;

        [Fact]
        public void TryHeal_BindsBareScalarToTheRealRequiredParameterName()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Action: read_file(C:\\dados\\nota.txt)", Tools(ReadFileTool()), out var healed)
                  .Should().BeTrue();

            healed.ToolName.Should().Be("read_file");

            using var doc = JsonDocument.Parse(healed.ArgumentsJson);
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            doc.RootElement.GetProperty("path").GetString().Should().Be("C:\\dados\\nota.txt");
        }

        [Fact]
        public void TryHeal_ProducesValidJsonEvenWhenTheScalarContainsQuotesAndBraces()
        {
            var healer = new RegexToolCallHealer();

            // Se os argumentos fossem montados por interpolação de string, isto geraria JSON inválido.
            healer.TryHeal("Action: read_file(\"C:\\a\"b\".txt)", Tools(ReadFileTool()), out var healed)
                  .Should().BeTrue();

            Action parse = () => JsonDocument.Parse(healed.ArgumentsJson).Dispose();
            parse.Should().NotThrow();
        }

        [Fact]
        public void TryHeal_ReadsNamedPairsUsingSchemaTypes()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Ação: count_lines(path=\"a.txt\", limit=10)", Tools(CountTool()), out var healed)
                  .Should().BeTrue();

            using var doc = JsonDocument.Parse(healed.ArgumentsJson);
            doc.RootElement.GetProperty("path").GetString().Should().Be("a.txt");
            doc.RootElement.GetProperty("limit").GetInt32().Should().Be(10);
        }

        [Fact]
        public void TryHeal_KeepsOnlySchemaPropertiesFromAJsonObject()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Action: read_file({\"path\": \"a.txt\", \"inventado\": 1})", Tools(ReadFileTool()), out var healed)
                  .Should().BeTrue();

            using var doc = JsonDocument.Parse(healed.ArgumentsJson);
            doc.RootElement.GetProperty("path").GetString().Should().Be("a.txt");
            doc.RootElement.TryGetProperty("inventado", out _).Should().BeFalse();
        }

        [Fact]
        public void TryHeal_RefusesWhenARequiredParameterIsMissing()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Action: read_file()", Tools(ReadFileTool()), out _).Should().BeFalse();
        }

        [Fact]
        public void TryHeal_AcceptsEmptyArgumentsWhenNothingIsRequired()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Action: ping()", Tools(NoArgTool()), out var healed).Should().BeTrue();
            healed.ArgumentsJson.Should().Be("{}");
        }

        [Fact]
        public void TryHeal_RefusesToolsThatAreNotActive()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Action: run_command(format c:)", Tools(ReadFileTool()), out _).Should().BeFalse();
        }

        [Fact]
        public void TryHeal_RefusesWhenThereIsNoMatch()
        {
            var healer = new RegexToolCallHealer();

            healer.TryHeal("Vou ler o arquivo agora.", Tools(ReadFileTool()), out _).Should().BeFalse();
        }
    }
}

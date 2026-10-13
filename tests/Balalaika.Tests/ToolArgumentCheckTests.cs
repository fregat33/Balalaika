using System.Text.Json;
using FluentAssertions;
using Balalaika.Workspace;

namespace Balalaika.Tests;

/// <summary>A tool call with a wrong argument name must come back with the names the tool accepts, not a bare error.</summary>
public sealed class ToolArgumentCheckTests
{
    private static readonly JsonElement SearchSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": { "pattern": { "type": "string" }, "kind": { "type": "string" }, "limit": { "type": "integer" } },
          "required": [ "pattern" ]
        }
        """).RootElement;

    [Fact]
    public void Describe_WhenRequiredArgumentIsMissing_NamesItAndTheAcceptedOnes()
    {
        // Act: the caller guessed 'query'
        string? message = ToolArgumentCheck.Describe("search_symbols", SearchSchema, ["query", "limit"]);

        // Assert
        message.Should().NotBeNull();
        message.Should().Contain("search_symbols: missing required argument 'pattern'");
        message.Should().Contain("Not accepted: 'query'");
        message.Should().Contain("belongs in 'pattern'");
        message.Should().Contain("Accepted arguments: pattern (required), kind, limit");
    }

    [Fact]
    public void Describe_WhenNoArgumentsAtAll_StillListsAccepted()
    {
        // Act
        string? message = ToolArgumentCheck.Describe("search_symbols", SearchSchema, []);

        // Assert
        message.Should().Contain("missing required argument 'pattern'");
        message.Should().NotContain("Not accepted");
        message.Should().Contain("Accepted arguments: pattern (required), kind, limit");
    }

    [Fact]
    public void Describe_WhenRequiredArgumentsPresent_IsNull()
    {
        // Act / Assert: extra unknown names are left for the tool binding to ignore
        ToolArgumentCheck.Describe("search_symbols", SearchSchema, ["pattern"]).Should().BeNull();
        ToolArgumentCheck.Describe("search_symbols", SearchSchema, ["pattern", "kind", "limit", "verbose"]).Should().BeNull();
    }

    [Fact]
    public void Remap_WhenOneUnknownStandsForTheOneMissing_NamesTheRename()
    {
        // Act: the caller guessed 'query' for 'pattern' and sent a valid 'limit' beside it
        ToolArgumentCheck.ArgumentRemap? remap = ToolArgumentCheck.Remap(SearchSchema, ["query", "limit"]);

        // Assert
        remap.Should().Be(new ToolArgumentCheck.ArgumentRemap("query", "pattern"));
    }

    [Fact]
    public void Remap_WhenTheCorrectionWouldBeAGuess_IsNull()
    {
        // Arrange: two required arguments
        JsonElement twoRequired = JsonDocument.Parse("""
            { "type": "object", "properties": { "symbol": { "type": "string" }, "solution": { "type": "string" } }, "required": [ "symbol", "solution" ] }
            """).RootElement;

        // Act / Assert
        ToolArgumentCheck.Remap(SearchSchema, []).Should().BeNull("nothing was sent in place of the missing one");
        ToolArgumentCheck.Remap(SearchSchema, ["query", "name"]).Should().BeNull("two unknown names cannot both be the pattern");
        ToolArgumentCheck.Remap(SearchSchema, ["pattern", "verbose"]).Should().BeNull("nothing is missing");
        ToolArgumentCheck.Remap(twoRequired, ["name"]).Should().BeNull("two required names are missing");
    }

    [Fact]
    public void Describe_WhenSchemaHasNoRequiredList_IsNull()
    {
        // Arrange
        JsonElement schema = JsonDocument.Parse("""{ "type": "object", "properties": { "solution": { "type": "string" } } }""").RootElement;

        // Act / Assert
        ToolArgumentCheck.Describe("status", schema, []).Should().BeNull();
    }
}

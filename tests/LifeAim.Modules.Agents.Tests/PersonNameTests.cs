using LifeAim.Modules.Agents.Application.Nodes;

namespace LifeAim.Modules.Agents.Tests;

public class PersonNameTests
{
    [Theory]
    [InlineData("rafael", "Rafael")]
    [InlineData("RAFAEL", "Rafael")]
    [InlineData("  rafael   reis ", "Rafael Reis")]
    [InlineData("rafael diniz dos santos", "Rafael Diniz dos Santos")]
    [InlineData("pedro alvarez de menezes", "Pedro Alvarez de Menezes")]
    [InlineData("maria da silva e souza", "Maria da Silva e Souza")]
    [InlineData("JOÃO DAS NEVES", "João das Neves")]
    [InlineData("ana-maria", "Ana-Maria")]
    [InlineData("sean o'neil", "Sean O'Neil")]
    [InlineData("joana d'ávila", "Joana d'Ávila")]
    [InlineData("élcio", "Élcio")]
    public void Capitalizes_each_word_except_the_connectors(string typed, string expected)
    {
        Assert.Equal(expected, PersonName.Capitalize(typed));
    }

    [Fact]
    public void A_connector_at_the_start_is_still_capitalized()
    {
        Assert.Equal("Da Silva", PersonName.Capitalize("da silva"));
    }

    [Fact]
    public void Mixed_case_typed_on_purpose_is_kept()
    {
        Assert.Equal("Ronald McDonald", PersonName.Capitalize("ronald McDonald"));
    }
}

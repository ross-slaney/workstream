using Workstream.Api.Domain;

namespace Workstream.Tests;

/// <summary>How the end of an agent's turn is read for the email about it.</summary>
[TestClass]
public sealed class TurnTests
{
    [TestMethod]
    public void A_turn_that_ends_on_a_question_needs_someone()
    {
        Assert.AreEqual(TurnEnding.NeedsYou, Turns.EndingOf("Should I deploy it?"));
        Assert.AreEqual(TurnEnding.NeedsYou, Turns.EndingOf("Which one do you mean, **the first or the second?**  \n"));
        Assert.AreEqual(TurnEnding.NeedsYou, Turns.EndingOf("Shall I run \"npm test?\""));
        Assert.AreEqual(TurnEnding.Finished, Turns.EndingOf("Done. Is it right? I think so."));
        Assert.AreEqual(TurnEnding.Finished, Turns.EndingOf("The server is on port 3000."));
        Assert.AreEqual(TurnEnding.Finished, Turns.EndingOf(""));
    }

    [TestMethod]
    public void An_email_quotes_a_long_message_up_to_a_word()
    {
        Assert.AreEqual("Short.", Turns.Excerpt("  Short.  "));
        var quoted = Turns.Excerpt(string.Join(' ', Enumerable.Repeat("word", 400)));
        Assert.IsTrue(quoted.Length <= Turns.ExcerptLength + 1, quoted.Length.ToString());
        Assert.IsTrue(quoted.EndsWith("word…", StringComparison.Ordinal), quoted);
    }
}

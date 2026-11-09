namespace InternetShop.MessageBroker;

public static class TopicMatcher
{
    public static bool Matches(string pattern, string routingKey) =>
        Match(pattern.Split('.'), routingKey.Split('.'), 0, 0);

    private static bool Match(
        string[] pattern,
        string[] key,
        int patternIndex,
        int keyIndex)
    {
        if (patternIndex == pattern.Length)
        {
            return keyIndex == key.Length;
        }

        if (pattern[patternIndex] == "#")
        {
            for (var next = keyIndex; next <= key.Length; next++)
            {
                if (Match(pattern, key, patternIndex + 1, next))
                {
                    return true;
                }
            }

            return false;
        }

        return keyIndex < key.Length
            && (pattern[patternIndex] == "*"
                || pattern[patternIndex] == key[keyIndex])
            && Match(pattern, key, patternIndex + 1, keyIndex + 1);
    }
}

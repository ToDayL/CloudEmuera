using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

//Regexをキャッシュする
static class RegexFactory
{
	static readonly Dictionary<string, Regex> _dictionary = [];

	// CloudEmuera: a pattern without regex syntax is equivalent to a literal
	// search under the default Regex options. Keep this check conservative so
	// patterns that may change matching semantics remain on the regex path.
	public static bool TryGetLiteralPattern(string regex, out string literal)
	{
		if (regex == null)
		{
			literal = null;
			return false;
		}

		foreach (char c in regex)
		{
			switch (c)
			{
				case '\\':
				case '^':
				case '$':
				case '.':
				case '*':
				case '+':
				case '?':
				case '(':
				case ')':
				case '[':
				case ']':
				case '{':
				case '}':
				case '|':
					literal = null;
					return false;
			}
		}

		literal = regex;
		return true;
	}

	public static Regex GetRegex(string regex)
	{
		if (_dictionary.TryGetValue(regex, out var ret))
		{
			return ret;
		}
		else
		{
			try
			{
				ret = new Regex(regex, RegexOptions.Compiled);
				_dictionary.Add(regex, ret);
			}
			catch (ArgumentException)
			{
				throw;
			}
		}

		return ret;

	}
}

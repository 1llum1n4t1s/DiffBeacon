std::string FilterLexer::UnescapeQuotes(char*& str, int& errorCode)
{
	errorCode = LEXER_ERR_UNTERMINATED_STRING;
	std::string result;
	while (*str != '\0')
	{
		if (*str == '"')
		{
			if (*(str + 1) == '"')
			{
				result += '"';
				str += 2;
			}
			else
			{
				str++;
				errorCode = 0;
				break;
			}
		}
		else
		{
			result += *str++;
		}
	}
	return result;
}
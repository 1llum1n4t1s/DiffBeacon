	"v\"" {
		int errorCode = 0;
		std::string str = UnescapeQuotes(YYCURSOR, errorCode);
		if (errorCode != 0)
			return errorCode;
		yylval.string = DupString(str.c_str());
		return TK_VERSION_LITERAL;
	}
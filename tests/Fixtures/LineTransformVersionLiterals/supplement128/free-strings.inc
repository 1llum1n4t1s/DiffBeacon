void FilterLexer::FreeStrings()
{
	for (auto str : strings)
		free(str);
	strings.clear();
}
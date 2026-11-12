String toTString(const std::string& str)
{
#ifdef UNICODE
	std::wstring wstr;
	UnicodeConverter::toUTF16(str, wstr);
	return wstr;
#else
	const char *p = convertUTF8toT(str.c_str(), str.length());
	std::string astr = p;
	dealloc((void *)p);
	return astr;
#endif
}

std::string toUTF8(const String& tstr)
{
	std::string u8str;
	toUTF8(tstr, u8str);
	return u8str;
}

void toUTF8(const String& tstr, std::string& u8str)
{
#ifdef _UNICODE
	const size_t len = tstr.length();
	u8str.resize(len * 3);

	if (len == 0)
		return;

	char *p = &u8str[0];
	for (size_t i = 0; i < len; ++i)
	{
		unsigned uc = tstr[i];
		if (uc >= 0xd800 && uc < 0xdc00)
		{
			if (++i != len)
			{
				wchar_t uc2 = tstr[i];
				uc = ((uc & 0x3ff) << 10) + (uc2 & 0x3ff) + 0x10000;
			}
		}
		p += Ucs4_to_Utf8(uc, reinterpret_cast<unsigned char *>(p));
	}
	u8str.resize(p - &u8str[0]);
#else
	const char *p = (const char *)convertTtoUTF8(tstr.c_str(), tstr.length());
	u8str = p;
	dealloc((void *)p);
#endif
}

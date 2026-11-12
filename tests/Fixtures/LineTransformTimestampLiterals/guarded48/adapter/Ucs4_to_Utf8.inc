int Ucs4_to_Utf8(unsigned unich, unsigned char * utf8)
{
	if (unich <= 0x7f)
	{
		utf8[0] = (unsigned char)unich;
		return 1;
	}
	else if (unich <= 0x7ff)
	{
		utf8[0] = 0xc0 + (unich >> 6);
		utf8[1] = 0x80 + (unich & 0x3f);
		return 2;
	}
	else if (unich <= 0xffff)
	{
		utf8[0] = 0xe0 + (unich >> 12);
		utf8[1] = 0x80 + ((unich >> 6) & 0x3f);
		utf8[2] = 0x80 + (unich & 0x3f);
		return 3;
	}
	else if (unich <= 0x1fffff)
	{
		utf8[0] = 0xf0 + (unich >> 18);
		utf8[1] = 0x80 + ((unich >> 12) & 0x3f);
		utf8[2] = 0x80 + ((unich >> 6) & 0x3f);
		utf8[3] = 0x80 + (unich & 0x3f);
		return 4;
	}
	else if (unich <= 0x3ffffff)
	{
		utf8[0] = 0xf8 + (unich >> 24);
		utf8[1] = 0x80 + ((unich >> 18) & 0x3f);
		utf8[2] = 0x80 + ((unich >> 12) & 0x3f);
		utf8[3] = 0x80 + ((unich >> 6) & 0x3f);
		utf8[4] = 0x80 + (unich & 0x3f);
		return 5;
	}
	else if (unich <= 0x7fffffff)
	{
		utf8[0] = 0xfc + (unich >> 30);
		utf8[1] = 0x80 + ((unich >> 24) & 0x3f);
		utf8[2] = 0x80 + ((unich >> 18) & 0x3f);
		utf8[3] = 0x80 + ((unich >> 12) & 0x3f);
		utf8[4] = 0x80 + ((unich >> 6) & 0x3f);
		utf8[5] = 0x80 + (unich & 0x3f);
		return 6;
	}
	else
	{
		// Invalid Unicode codepoint (high bit was set)
		// TODO: What do we do ?
		utf8[0] = '?';
		return 1;
	}
}

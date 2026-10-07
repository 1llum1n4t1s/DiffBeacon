struct Comp02Functor
{
	Comp02Functor(const file_data * inf10, const file_data * inf12) :
		inf10_(inf10), inf12_(inf12)
	{
	}
	bool operator()(const DiffRangeInfo &dr3)
	{
		int line0 = dr3.begin[0];
		int line2 = dr3.begin[2];
		int line0end = dr3.end[0];
		int line2end = dr3.end[2];
		if (line0end - line0 != line2end - line2)
			return false;
		const char **linbuf0 = inf10_[1].linbuf + inf10_[1].linbuf_base;
		const char **linbuf2 = inf12_[1].linbuf + inf12_[1].linbuf_base;
		for (int i = 0; i < line0end - line0 + 1; ++i)
		{
			const size_t line0len = linbuf0[line0 + i + 1] - linbuf0[line0 + i];
			const size_t line2len = linbuf2[line2 + i + 1] - linbuf2[line2 + i];
			if (line_cmp(linbuf0[line0 + i], line0len, linbuf2[line2 + i], line2len) != 0)
				return false;
		}
		return true;
	}
	const file_data *inf10_;
	const file_data *inf12_;
};

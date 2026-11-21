void CDiffWrapper::AddDiffRange(DiffList *pDiffList, unsigned begin0, unsigned end0, unsigned begin1, unsigned end1, OP_TYPE op)
{
	try
	{
		DIFFRANGE dr;
		dr.begin[0] = begin0;
		dr.end[0] = end0;
		dr.begin[1] = begin1;
		dr.end[1] = end1;
		dr.begin[2] = -1;
		dr.end[2] = -1;
		dr.op = op;
		dr.blank[0] = dr.blank[1] = dr.blank[2] = -1;
		pDiffList->AddDiff(dr);
	}
	catch (std::exception& e)
	{
		RootLogger::Error(e.what());
	}
}

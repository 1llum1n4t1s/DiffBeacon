bool CDiffWrapper::IsIdenticalOrIgnorable(struct change* script)
{
	bool diff = false;
	struct change *e=nullptr, *p=nullptr;
	for (e = script; e != nullptr; e = p)
	{
		if (!e->trivial)
			diff = true;
		p = e->link;
	}
	return !diff;
}

VersionLiteral::VersionLiteral(const std::string& v)
{
	int major = 0, minor = 0, build = 0, revision = 0;
	sscanf_s(v.c_str(), "%d.%d.%d.%d", &major, &minor, &build, &revision);
	value = (static_cast<int64_t>(major) << 48) + (static_cast<int64_t>(minor) << 32) + (static_cast<int64_t>(build) << 16) + revision;
}
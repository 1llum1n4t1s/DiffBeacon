static std::optional<int64_t> getConstIntValue(const ExprNode* node)
{
	if (auto intNode = dynamic_cast<const IntLiteral*>(node))
		return intNode->value;
	if (auto sizeNode = dynamic_cast<const SizeLiteral*>(node))
		return sizeNode->value;
	if (auto durationNode = dynamic_cast<const DurationLiteral*>(node))
		return durationNode->value;
	if (auto versionNode = dynamic_cast<const VersionLiteral*>(node))
		return versionNode->value;
	return std::nullopt;
}
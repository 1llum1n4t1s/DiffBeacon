struct ExprNode
{
	virtual ~ExprNode() { }
	virtual ExprNode* Optimize() { return this; }
	virtual ValueType Evaluate(const FilterEvalContext& ectxt) const = 0;
};

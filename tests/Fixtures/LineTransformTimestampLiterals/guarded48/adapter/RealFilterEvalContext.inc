class CDiffContext;
class DIFFITEM;
struct ExprNode;
struct YYSTYPE;
struct ILineDataProvider;
struct FilterExpression;
struct FilterSharedContext;

struct FilterEvalContext
{
	const FilterExpression* expr = nullptr;
	const DIFFITEM* di = nullptr;
	const ILineDataProvider* provider = nullptr;
	const FilterSharedContext* sharedContext = nullptr;
	int lineIndex = -1;
};

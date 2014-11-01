﻿namespace Simple.Data
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Dynamic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Commands;
    using Extensions;
    using Operations;
    using QueryPolyfills;

    public class SimpleQuery : DynamicObject, IEnumerable
    {
        private readonly Adapter _adapter;

        private readonly SimpleQueryClauseBase[] _clauses;
        private readonly string _tableName;
        private readonly bool _singleton;
        private Func<IObservable<dynamic>> _asObservableImplementation;
        private DataStrategy _dataStrategy;
        private JoinClause _tempJoinWaitingForOn;

        public SimpleQuery(DataStrategy dataStrategy, string tableName)
        {
            _dataStrategy = dataStrategy;
            if (_dataStrategy != null)
                _adapter = _dataStrategy.GetAdapter();
            _tableName = tableName;
            _clauses = new SimpleQueryClauseBase[0];
        }

        public SimpleQuery(Adapter adapter, DataStrategy dataStrategy, string tableName)
        {
            _adapter = adapter;
            _dataStrategy = dataStrategy;
            _tableName = tableName;
            _clauses = new SimpleQueryClauseBase[0];
        }

        private SimpleQuery(SimpleQuery source,
                            SimpleQueryClauseBase[] clauses)
        {
            _adapter = source._adapter;
            _dataStrategy = source._dataStrategy;
            _tableName = source.TableName;
            _singleton = source._singleton;
            _clauses = clauses;
        }

        private SimpleQuery(SimpleQuery source, SingletonIndicator singletonIndicator)
        {
            _adapter = source._adapter;
            _dataStrategy = source._dataStrategy;
            _tableName = source.TableName;
            _clauses = source._clauses;
            _singleton = true;
        }

        private SimpleQuery(SimpleQuery source,
                            string tableName,
                            SimpleQueryClauseBase[] clauses)
        {
            _adapter = source._adapter;
            _dataStrategy = source._dataStrategy;
            _tableName = tableName;
            _clauses = clauses;
            _singleton = source._singleton;
        }

        public IEnumerable<SimpleQueryClauseBase> Clauses
        {
            get { return _clauses; }
        }

        public string TableName
        {
            get { return _tableName; }
        }

        #region IEnumerable Members

        public IEnumerator GetEnumerator()
        {
            return Run().Result.GetEnumerator();
        }

        #endregion

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            if (_tempJoinWaitingForOn != null && _tempJoinWaitingForOn.Name.Equals(binder.Name))
            {
                result = _tempJoinWaitingForOn.Table;
            }
            else
            {
                var join = _clauses.OfType<JoinClause>().FirstOrDefault(j => j.Name.Equals(binder.Name));
                if (join != null)
                {
                    result = join.Table;
                }
                else
                {
                    result = new SimpleQuery(this, _tableName + "." + binder.Name,
                                             (SimpleQueryClauseBase[]) _clauses.Clone());
                }
            }
            return true;
        }

        public override bool TryConvert(ConvertBinder binder, out object result)
        {
            if (binder.Type == typeof(IEnumerable<dynamic>))
            {
                result = Cast<dynamic>();
                return true;
            }

            var collectionType = binder.Type.GetInterface("ICollection`1");
            if (collectionType != null)
            {
                if (TryConvertToGenericCollection(binder, out result, collectionType)) return true;
            }

            if (binder.Type.Name.Equals("IEnumerable`1"))
            {
                var genericArguments = binder.Type.GetGenericArguments();
                var cast =
                    typeof (SimpleQuery).GetMethod("Cast").MakeGenericMethod(genericArguments);
                result = cast.Invoke(this, null);
                return true;
            }

            return base.TryConvert(binder, out result);
        }

        private bool TryConvertToGenericCollection(ConvertBinder binder, out object result, Type collectionType)
        {
            var genericArguments = collectionType.GetGenericArguments();
            var enumerableConstructor =
                binder.Type.GetConstructor(new[]
                                               {
                                                   typeof (IEnumerable<>).MakeGenericType(
                                                       genericArguments)
                                               });
            if (enumerableConstructor != null)
            {
                var cast =
                    typeof (SimpleQuery).GetMethod("Cast").MakeGenericMethod(genericArguments);
                result = Activator.CreateInstance(binder.Type, cast.Invoke(this, null));
                return true;
            }

            var defaultConstructor = binder.Type.GetConstructor(new Type[0]);
            if (defaultConstructor != null)
            {
                result = Activator.CreateInstance(binder.Type);
                var add = binder.Type.GetMethod("Add", genericArguments);
                var cast =
                    typeof (SimpleQuery).GetMethod("Cast").MakeGenericMethod(genericArguments);
                foreach (var item in (IEnumerable) cast.Invoke(this, null))
                {
                    add.Invoke(result, new[] {item});
                }
                return true;
            }
            result = null;
            return false;
        }

        /// <summary>
        /// Selects only the specified columns.
        /// </summary>
        /// <param name="columns">The columns.</param>
        /// <returns>A new <see cref="SimpleQuery"/> which will select only the specified columns.</returns>
        public SimpleQuery Select(params SimpleReference[] columns)
        {
            return Select(columns.AsEnumerable());
        }

        /// <summary>
        /// Selects only the specified columns.
        /// </summary>
        /// <param name="columns">The columns.</param>
        /// <returns>A new <see cref="SimpleQuery"/> which will select only the specified columns.</returns>
        public SimpleQuery Select(IEnumerable<SimpleReference> columns)
        {
            ThrowIfThereIsAlreadyASelectClause();
            return new SimpleQuery(this, _clauses.Append(new SelectClause(columns)));
        }

        /// <summary>
        /// Selects only the specified columns.
        /// </summary>
        /// <param name="columns">The columns.</param>
        /// <returns>A new <see cref="SimpleQuery"/> which will select only the specified columns.</returns>
        public SimpleQuery ReplaceSelect(params SimpleReference[] columns)
        {
            return ReplaceSelect(columns.AsEnumerable());
        }

        /// <summary>
        /// Selects only the specified columns.
        /// </summary>
        /// <param name="columns">The columns.</param>
        /// <returns>A new <see cref="SimpleQuery"/> which will select only the specified columns.</returns>
        public SimpleQuery ReplaceSelect(IEnumerable<SimpleReference> columns)
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is SelectClause)).Append(new SelectClause(columns)).ToArray());
        }

        /// <summary>
        /// Alters the query to lock the rows for update. 
        /// </summary>
        /// <param name="skipLockedRows">Indicates whether the selection should skip rows already locked</param>
        /// <returns>A new <see cref="SimpleQuery"/> which will perform locking on the selected rows</returns>
        public SimpleQuery ForUpdate(bool skipLockedRows)
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is ForUpdateClause)).Append(new ForUpdateClause(skipLockedRows)).ToArray());
        }

        /// <summary>
        /// Removes any specified ForUpdate from the Query
        /// </summary>
        /// <returns>A new <see cref="SimpleQuery"/> with any specified ForUpdate removed</returns>
        public SimpleQuery ClearForUpdate()
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is ForUpdateClause)).ToArray());
        }

        private void ThrowIfThereIsAlreadyASelectClause()
        {
            if (_clauses.OfType<SelectClause>().Any())
                throw new InvalidOperationException("Query already contains a Select clause.");
        }

        public SimpleQuery ReplaceWhere(SimpleExpression criteria)
        {
            return new SimpleQuery(this,
                                   _clauses.Where(c => !(c is WhereClause)).Append(new WhereClause(criteria)).ToArray());
        }

        public SimpleQuery Where(SimpleExpression criteria)
        {
            if (criteria == null) throw new ArgumentNullException("criteria");
            return new SimpleQuery(this, _clauses.Append(new WhereClause(criteria)));
        }

        public SimpleQuery OrderBy(ObjectReference reference, OrderByDirection? direction = null)
        {
            return new SimpleQuery(this, _clauses.Append(new OrderByClause(reference, direction)));
        }

        public SimpleQuery OrderBy(params ObjectReference[] references)
        {
            if (references.Length == 0)
            {
                throw new ArgumentException("OrderBy requires parameters");
            }
            var q = this.OrderBy(references[0]);
            foreach (var reference in references.Skip(1))
            {
                q = q.ThenBy(reference);
            }
            return q;
        }

        public SimpleQuery OrderByDescending(ObjectReference reference)
        {
            return new SimpleQuery(this, _clauses.Append(new OrderByClause(reference, OrderByDirection.Descending)));
        }

        public SimpleQuery ThenBy(ObjectReference reference, OrderByDirection? direction = null)
        {
            ThrowIfNoOrderByClause("ThenBy requires an existing OrderBy");

            return new SimpleQuery(this, _clauses.Append(new OrderByClause(reference, direction)));
        }

        private void ThrowIfNoOrderByClause(string message)
        {
            if (_clauses == null || !_clauses.OfType<OrderByClause>().Any())
            {
                throw new InvalidOperationException(message);
            }
        }

        public SimpleQuery ThenByDescending(ObjectReference reference)
        {
            return ThenBy(reference, OrderByDirection.Descending);
        }

        public SimpleQuery Skip(int skip)
        {
            return new SimpleQuery(this, _clauses.ReplaceOrAppend(new SkipClause(skip)));
        }

        public SimpleQuery ClearSkip()
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is SkipClause)).ToArray());
        }

        public SimpleQuery Take(int take)
        {
            return new SimpleQuery(this, _clauses.ReplaceOrAppend(new TakeClause(take)));
        }

        public SimpleQuery Distinct()
        {
            return new SimpleQuery(this, _clauses.ReplaceOrAppend(new DistinctClause()));
        }

        public SimpleQuery ClearTake()
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is TakeClause)).ToArray());
        }

        public SimpleQuery ClearWithTotalCount()
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is WithCountClause)).ToArray());
        }

        protected async Task<IEnumerable<dynamic>> Run()
        {
            IEnumerable<SimpleQueryClauseBase> unhandledClauses;
            var result = (QueryResult)(await _dataStrategy.Run.Execute(new QueryOperation(this)));

            if (result.UnhandledClauses != null)
            {
                var unhandledClausesList = result.UnhandledClauses.ToList();
                if (unhandledClausesList.Count > 0)
                {
                    result = new QueryResult(new DictionaryQueryRunner(_tableName, result.Data, unhandledClausesList).Run());
                }
            }

            return SimpleResultSet.Create(result.Data, _tableName, _dataStrategy).Cast<dynamic>();
        }

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            if (base.TryInvokeMember(binder, args, out result))
            {
                return true;
            }
            if (binder.Name.StartsWith("order", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 0)
                {
                    throw new ArgumentException("OrderByColumn form does not accept parameters");
                }
                result = ParseOrderBy(binder.Name);
                return true;
            }
            if (binder.Name.StartsWith("then", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 0)
                {
                    throw new ArgumentException("ThenByColumn form does not accept parameters");
                }
                result = ParseThenBy(binder.Name);
                return true;
            }
            if (binder.Name.Equals("join", StringComparison.OrdinalIgnoreCase))
            {
                result = args.Length == 1 ? Join(ObjectAsObjectReference(args[0]), JoinType.Inner) : ParseJoin(binder, args);
                return true;
            }
            if (binder.Name.Equals("leftjoin", StringComparison.OrdinalIgnoreCase) || binder.Name.Equals("outerjoin", StringComparison.OrdinalIgnoreCase))
            {
                result = args.Length == 1 ? Join(ObjectAsObjectReference(args[0]), JoinType.Outer) : ParseJoin(binder, args);
                return true;
            }
            if (binder.Name.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                result = ParseOn(binder, args);
                return true;
            }
            if (binder.Name.Equals("having", StringComparison.OrdinalIgnoreCase))
            {
                SimpleExpression expression;
                try
                {
                    expression = args.SingleOrDefault() as SimpleExpression;
                }
                catch (InvalidOperationException)
                {
                    throw new ArgumentException("Having requires an expression");
                }
                if (expression != null)
                {
                    result = new SimpleQuery(this, _clauses.Append(new HavingClause(expression)));
                    return true;
                }
                throw new ArgumentException("Having requires an expression");
            }
            if (binder.Name.StartsWith("with", StringComparison.OrdinalIgnoreCase) && !binder.Name.Equals("WithTotalCount", StringComparison.OrdinalIgnoreCase))
            {
                result = ParseWith(binder, args);
                return true;
            }
            if (binder.Name.Equals("select", StringComparison.OrdinalIgnoreCase))
            {
                result = Select(args.OfType<SimpleReference>());
                return true;
            }

            var command = Commands.CommandFactory.GetCommandFor(binder.Name) as IQueryCompatibleCommand;
            if (command != null)
            {
                try
                {
                    result = command.Execute(_dataStrategy, this, binder, args);
                    return true;
                }
                catch (NotImplementedException)
                {
                }
            }
            try
            {
                var methodInfo = typeof(SimpleQuery).GetMethod(binder.Name, args.Select(a => (a ?? new object()).GetType()).ToArray());
                if (methodInfo != null)
                {
                    methodInfo.Invoke(this, args);
                }
            }
            catch (AmbiguousMatchException)
            {
            }

            if (binder.Name.Equals("where", StringComparison.InvariantCultureIgnoreCase) || binder.Name.Equals("replacewhere", StringComparison.InvariantCultureIgnoreCase))
            {
                throw new BadExpressionException("Where methods require a single criteria expression.");
            }

            return false;
        }

        public SimpleQuery With(ObjectReference reference, out dynamic queryObjectReference)
        {
            queryObjectReference = reference;
            return With(new[] {reference});
        }

        public SimpleQuery WithOne(ObjectReference reference, out dynamic queryObjectReference)
        {
            queryObjectReference = reference;
            return With(new[] {reference}, WithType.One);
        }

        public SimpleQuery WithMany(ObjectReference reference, out dynamic queryObjectReference)
        {
            queryObjectReference = reference;
            return With(new[] {reference}, WithType.Many);
        }

        private SimpleQuery ParseWith(InvokeMemberBinder binder, object[] args)
        {
            if (args.Length > 0)
            {
                if (binder.Name.Equals("with", StringComparison.OrdinalIgnoreCase))
                {
                    return With(args);
                }

                if (binder.Name.Equals("withone", StringComparison.OrdinalIgnoreCase))
                {
                    return With(args, WithType.One);
                }

                if (binder.Name.Equals("withmany", StringComparison.OrdinalIgnoreCase))
                {
                    return With(args, WithType.Many);
                }

                throw new ArgumentException("WithTable form does not accept parameters");
            }

            var objectName = binder.Name.Substring(4);
            if (string.IsNullOrWhiteSpace(objectName))
            {
                throw new ArgumentException("With requires a Table reference");
            }
            var withClause = new WithClause(new ObjectReference(objectName, new ObjectReference(_tableName, _dataStrategy), _dataStrategy));
            return new SimpleQuery(this, _clauses.Append(withClause));
        }

        private SimpleQuery With(IEnumerable<object> args, WithType withType = WithType.NotSpecified)
        {
            var clauses = new List<SimpleQueryClauseBase>(_clauses);
            clauses.AddRange(args.OfType<ObjectReference>().Select(reference => new WithClause(reference, withType)));
            return new SimpleQuery(this, clauses.ToArray());
        }

        private ObjectReference ObjectAsObjectReference(object o)
        {
            var objectReference = o as ObjectReference;
            if (!ReferenceEquals(objectReference, null)) return objectReference;

            var dynamicTable = o as DynamicTable;
            if (dynamicTable != null) return new ObjectReference(dynamicTable.GetName(), _dataStrategy);

            throw new InvalidOperationException("Could not convert parameter to ObjectReference.");
        }

        public SimpleQuery Join(ObjectReference objectReference, JoinType joinType)
        {
            if (ReferenceEquals(objectReference, null)) throw new ArgumentNullException("objectReference");
            _tempJoinWaitingForOn = new JoinClause(objectReference, joinType, null);

            return this;
        }

        public SimpleQuery Join(ObjectReference objectReference, out dynamic queryObjectReference)
        {
            return Join(objectReference, JoinType.Inner, out queryObjectReference);
        }

        public SimpleQuery Join(ObjectReference objectReference, JoinType joinType, out dynamic queryObjectReference)
        {
            var newJoin = new JoinClause(objectReference, null);
            _tempJoinWaitingForOn = newJoin;
            queryObjectReference = objectReference;

            return this;
        }

        public SimpleQuery LeftJoin(ObjectReference objectReference)
        {
            return OuterJoin(objectReference);
        }

        public SimpleQuery LeftJoin(ObjectReference objectReference, out dynamic queryObjectReference)
        {
            return OuterJoin(objectReference, out queryObjectReference);
        }

        public SimpleQuery OuterJoin(ObjectReference objectReference)
        {
            if (ReferenceEquals(objectReference, null)) throw new ArgumentNullException("objectReference");
            _tempJoinWaitingForOn = new JoinClause(objectReference, JoinType.Outer);

            return this;
        }

        public SimpleQuery OuterJoin(ObjectReference objectReference, out dynamic queryObjectReference)
        {
            _tempJoinWaitingForOn = new JoinClause(objectReference, JoinType.Outer);
            queryObjectReference = objectReference;

            return this;
        }

        public SimpleQuery Join(DynamicTable dynamicTable, JoinType joinType)
        {
            if (ReferenceEquals(dynamicTable, null)) throw new ArgumentNullException("dynamicTable");
            _tempJoinWaitingForOn = new JoinClause(dynamicTable.ToObjectReference(), joinType, null);

            return this;
        }

        public SimpleQuery Join(DynamicTable dynamicTable, out dynamic queryObjectReference)
        {
            return Join(dynamicTable, JoinType.Inner, out queryObjectReference);
        }

        public SimpleQuery Join(DynamicTable dynamicTable, JoinType joinType, out dynamic queryObjectReference)
        {
            if (ReferenceEquals(dynamicTable, null)) throw new ArgumentNullException("dynamicTable");
            var newJoin = new JoinClause(dynamicTable.ToObjectReference(), null);
            _tempJoinWaitingForOn = newJoin;
            queryObjectReference = dynamicTable.ToObjectReference();

            return this;
        }

        public SimpleQuery LeftJoin(DynamicTable dynamicTable)
        {
            return OuterJoin(dynamicTable);
        }

        public SimpleQuery LeftJoin(DynamicTable dynamicTable, out dynamic queryObjectReference)
        {
            return OuterJoin(dynamicTable, out queryObjectReference);
        }

        public SimpleQuery OuterJoin(DynamicTable dynamicTable)
        {
            if (ReferenceEquals(dynamicTable, null)) throw new ArgumentNullException("dynamicTable");
            _tempJoinWaitingForOn = new JoinClause(dynamicTable.ToObjectReference(), JoinType.Outer);

            return this;
        }

        public SimpleQuery OuterJoin(DynamicTable dynamicTable, out dynamic queryObjectReference)
        {
            _tempJoinWaitingForOn = new JoinClause(dynamicTable.ToObjectReference(), JoinType.Outer);
            queryObjectReference = dynamicTable;

            return this;
        }

        public SimpleQuery On(SimpleExpression joinExpression)
        {
            if (_tempJoinWaitingForOn == null)
            {
                throw new InvalidOperationException("Call to On must be preceded by call to JoinInfo.");
            }
            if (ReferenceEquals(joinExpression, null))
            {
                throw new BadExpressionException("On expects an expression or named parameters.");
            }
            return AddNewJoin(new JoinClause(_tempJoinWaitingForOn.Table, _tempJoinWaitingForOn.JoinType, joinExpression));
        }

        [Obsolete]
        public SimpleQuery WithTotalCount(out Future<int> count)
        {
            Action<int> setCount;
            count = Future<int>.Create(out setCount);
            return new SimpleQuery(this, _clauses.Append(new WithCountClause(setCount)));
        }

        public SimpleQuery WithTotalCount(out Promise<int> count)
        {
            Action<int> setCount;
            count = Promise<int>.Create(out setCount);
            return new SimpleQuery(this, _clauses.Append(new WithCountClause(setCount)));
        }

        private SimpleQuery ParseJoin(InvokeMemberBinder binder, object[] args)
        {
            var tableToJoin = args[0] as ObjectReference;
            if (ReferenceEquals(tableToJoin, null))
            {
                var dynamicTable = args[0] as DynamicTable;
                if (!ReferenceEquals(dynamicTable, null))
                {
                    tableToJoin = dynamicTable.ToObjectReference();
                }
            }
            if (tableToJoin == null) throw new BadJoinExpressionException("Incorrect join table specified");
            if (HomogenizedEqualityComparer.DefaultInstance.Equals(tableToJoin.GetAliasOrName(), _tableName))
            {
                throw new BadJoinExpressionException("Cannot join unaliased table to itself.");
            }

            SimpleExpression joinExpression = null;

            if (binder.CallInfo.ArgumentNames.Any(s => !string.IsNullOrWhiteSpace(s)))
            {
                joinExpression = ExpressionHelper.CriteriaDictionaryToExpression(tableToJoin,
                                                                                 binder.NamedArgumentsToDictionary(args));
            }
            else if (args.Length == 2)
            {
                joinExpression = args[1] as SimpleExpression;
            }

            if (joinExpression == null) throw new BadJoinExpressionException("Could not create join expression");

            var type = binder.Name.Equals("join", StringComparison.OrdinalIgnoreCase) ? JoinType.Inner : JoinType.Outer;
            var newJoin = new JoinClause(tableToJoin, type, joinExpression);

            return AddNewJoin(newJoin);
        }

        private SimpleQuery ParseOn(InvokeMemberBinder binder, IEnumerable<object> args)
        {
            if (_tempJoinWaitingForOn == null)
            {
                throw new InvalidOperationException("Call to On must be preceded by call to JoinInfo.");
            }
            var namedArguments = binder.NamedArgumentsToDictionary(args);
            if (namedArguments == null || namedArguments.Count == 0)
            {
                throw new BadExpressionException("On expects an expression or named parameters.");
            }
            var joinExpression = ExpressionHelper.CriteriaDictionaryToExpression(_tempJoinWaitingForOn.Table,
                                                                                 namedArguments);
            return AddNewJoin(new JoinClause(_tempJoinWaitingForOn.Table, _tempJoinWaitingForOn.JoinType, joinExpression));
        }

        private SimpleQuery AddNewJoin(JoinClause newJoin)
        {
            _tempJoinWaitingForOn = null;
            return new SimpleQuery(this, _clauses.Append(newJoin));
        }

        private SimpleQuery ParseOrderBy(string methodName)
        {
            methodName = Regex.Replace(methodName, "^order_?by_?", "", RegexOptions.IgnoreCase);
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException("Invalid arguments to OrderBy");
            }
            if (methodName.EndsWith("descending", StringComparison.OrdinalIgnoreCase))
            {
                methodName = Regex.Replace(methodName, "_?descending$", "", RegexOptions.IgnoreCase);
                if (string.IsNullOrWhiteSpace(methodName))
                {
                    throw new ArgumentException("Invalid arguments to OrderByDescending");
                }
                return OrderByDescending(ObjectReference.FromString(_tableName + "." + methodName));
            }
            return OrderBy(ObjectReference.FromString(_tableName + "." + methodName));
        }

        private SimpleQuery ParseThenBy(string methodName)
        {
            ThrowIfNoOrderByClause("Must call OrderBy before ThenBy");
            methodName = Regex.Replace(methodName, "^then_?by_?", "", RegexOptions.IgnoreCase);
            if (methodName.EndsWith("descending", StringComparison.OrdinalIgnoreCase))
            {
                methodName = Regex.Replace(methodName, "_?descending$", "", RegexOptions.IgnoreCase);
                return ThenByDescending(ObjectReference.FromString(_tableName + "." + methodName));
            }
            return ThenBy(ObjectReference.FromString(_tableName + "." + methodName));
        }

        public async Task<IEnumerable<T>> Cast<T>()
        {
            return new CastEnumerable<T>(await Run());
        }

        public async Task<IEnumerable<T>> OfType<T>()
        {
            return new OfTypeEnumerable<T>(await Run());
        }

        public async Task<IList<dynamic>> ToList()
        {
            return (await Run()).ToList();
        }

        public async Task<dynamic[]> ToArray()
        {
            return (await Run()).ToArray();
        }

        public async Task<dynamic> ToScalar()
        {
            var data = (await Run()).OfType<IDictionary<string, object>>().ToArray();
            if (data.Length == 0)
            {
                throw new SimpleDataException("Query returned no rows; cannot return scalar value.");
            }
            if (data[0].Count == 0)
            {
                throw new SimpleDataException("Selected row contains no values; cannot return scalar value.");
            }
            return data[0].First().Value;
        }

        public async Task<dynamic> ToScalarOrDefault()
        {
            var data = (await Run()).OfType<IDictionary<string, object>>().ToArray();
            if (data.Length == 0)
            {
                return null;
            }
            if (data[0].Count == 0)
            {
                return null;
            }
            return data[0].First().Value;
        }

        public async Task<List<dynamic>> ToScalarList()
        {
            return (await ToScalarEnumerable()).ToList();
        }

        public async Task<dynamic[]> ToScalarArray()
        {
            return (await ToScalarEnumerable()).ToArray();
        }

        public async Task<List<T>> ToScalarList<T>()
        {
            return (await ToScalarEnumerable()).Cast<T>().ToList();
        }

        public async Task<T[]> ToScalarArray<T>()
        {
            return (await ToScalarEnumerable()).Cast<T>().ToArray();
        }

        private async Task<IEnumerable<dynamic>> ToScalarEnumerable()
        {
            return (await Run()).OfType<IDictionary<string, object>>().Select(dict => dict.Values.FirstOrDefault());
        }

        public async Task<IList<T>> ToList<T>()
        {
            return (await Cast<T>()).ToList();
        }

        public async Task<T[]> ToArray<T>()
        {
            return (await Cast<T>()).ToArray();
        }

        public async Task<T> ToScalar<T>()
        {
            return (T) (await ToScalar());
        }

        public async Task<T> ToScalarOrDefault<T>()
        {
            return (await ToScalarOrDefault()) ?? default(T);
        }

        public async Task<dynamic> First()
        {
            return (await Take(1).Run()).First();
        }

        public async Task<dynamic> FirstOrDefault()
        {
            return (await Take(1).Run()).FirstOrDefault();
        }

        public async Task<T> First<T>()
        {
            return (await Take(1).Cast<T>()).First();
        }

        public async Task<T> FirstOrDefault<T>()
        {
            return (await Take(1).Cast<T>()).FirstOrDefault();
        }

        public async Task<T> First<T>(Func<T, bool> predicate)
        {
            return (await Cast<T>()).First(predicate);
        }

        public async Task<T> FirstOrDefault<T>(Func<T, bool> predicate)
        {
            return (await Cast<T>()).FirstOrDefault(predicate);
        }

        public async Task<dynamic> Single()
        {
            return (await Take(1).Run()).First();
        }

        public async Task<dynamic> SingleOrDefault()
        {
            return (await Take(1).Run()).FirstOrDefault();
        }

        public async Task<T> Single<T>()
        {
            return (await Take(1).Cast<T>()).Single();
        }

        public async Task<T> SingleOrDefault<T>()
        {
            return (await Take(1).Cast<T>()).SingleOrDefault();
        }

        public async Task<T> Single<T>(Func<T, bool> predicate)
        {
            return (await Cast<T>()).Single(predicate);
        }

        public async Task<T> SingleOrDefault<T>(Func<T, bool> predicate)
        {
            return (await Cast<T>()).SingleOrDefault(predicate);
        }

        public async Task<int> Count()
        {
            return Convert.ToInt32(await Select(new CountSpecialReference()).ToScalar());
        }

        /// <summary>
        /// Checks whether the query matches any records without running the full query.
        /// </summary>
        /// <returns><c>true</c> if the query matches any record; otherwise, <c>false</c>.</returns>
        public async Task<bool> Exists()
        {
            return (await Select(new ExistsSpecialReference()).Run()).Count() == 1;
        }

        /// <summary>
        /// Checks whether the query matches any records without running the full query.
        /// </summary>
        /// <returns><c>true</c> if the query matches any record; otherwise, <c>false</c>.</returns>
        /// <remarks>This method is an alias for <see cref="Exists"/>.</remarks>
        public Task<bool> Any()
        {
            return Exists();
        }

        public void SetDataStrategy(DataStrategy dataStrategy)
        {
            _dataStrategy = dataStrategy;
        }

        public IObservable<dynamic> AsObservable()
        {
            throw new NotImplementedException();
        }

        public SimpleQuery ClearOrderBy()
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is OrderByClause)).ToArray());
        }

        public SimpleQuery ClearWith()
        {
            return new SimpleQuery(this, _clauses.Where(c => !(c is WithClause)).ToArray());
        }

        public SimpleQuery Singleton()
        {
            return new SimpleQuery(this, default(SingletonIndicator));
        }

        private async Task<dynamic> RunSingleton()
        {
            var list = await Take(1).Run();
            return list.FirstOrDefault();
        }

        public dynamic GetAwaiter()
        {
            if (_singleton)
            {
                return RunSingleton().GetAwaiter();
            }
            else
            {
                return Run().GetAwaiter();
            }
        }

        public dynamic RunSync()
        {
            if (_singleton)
            {
                return RunSingleton().Result;
            }
            else
            {
                var enumerable = Run().Result;
                return new SimpleList<dynamic>(enumerable.ToList());
            }
        }

        private struct SingletonIndicator
        {
        }
    }

    public class SimpleList<T> : DynamicObject, IList<T>
    {
        private readonly IList<T> _wrapped;

        public override bool TryInvokeMember(InvokeMemberBinder binder, object[] args, out object result)
        {
            if (!base.TryInvokeMember(binder, args, out result))
            {
                var extArgs = new object[args.Length + 1];
                var types = new Type[args.Length + 1];
                extArgs[0] = _wrapped;
                types[0] = typeof (IEnumerable<T>);
                for (int i = 0; i < args.Length; i++)
                {
                    extArgs[i] = args[i];
                    if (args[i] != null)
                    {
                        types[i + 1] = args[i].GetType();
                    }
                }
                var method = typeof (Enumerable).GetMethod(binder.Name, BindingFlags.Static | BindingFlags.Public, null, types, null);
                if (method != null)
                {
                    var typeArg = binder.GetGenericParameter();
                    if (typeArg != null)
                    {
                        method = method.MakeGenericMethod(typeArg);
                    }
                    result = method.Invoke(null, extArgs);
                    return true;
                }
            }
            return false;
        }

        public dynamic FirstOrDefault()
        {
            return _wrapped.FirstOrDefault();
        }

        public IEnumerable<TCast> Cast<TCast>()
        {
            foreach (dynamic d in _wrapped)
            {
                TCast c = d;
                yield return c;
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            return _wrapped.GetEnumerator();
        }

        public void Add(T item)
        {
            _wrapped.Add(item);
        }

        public void Clear()
        {
            _wrapped.Clear();
        }

        public bool Contains(T item)
        {
            return _wrapped.Contains(item);
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            _wrapped.CopyTo(array, arrayIndex);
        }

        public bool Remove(T item)
        {
            return _wrapped.Remove(item);
        }

        public int Count
        {
            get { return _wrapped.Count; }
        }

        public bool IsReadOnly
        {
            get { return _wrapped.IsReadOnly; }
        }

        public int IndexOf(T item)
        {
            return _wrapped.IndexOf(item);
        }

        public void Insert(int index, T item)
        {
            _wrapped.Insert(index, item);
        }

        public void RemoveAt(int index)
        {
            _wrapped.RemoveAt(index);
        }

        public T this[int index]
        {
            get { return _wrapped[index]; }
            set { _wrapped[index] = value; }
        }

        public SimpleList(IList<T> wrapped)
        {
            _wrapped = wrapped;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
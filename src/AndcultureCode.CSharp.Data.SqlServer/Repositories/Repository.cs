using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AndcultureCode.CSharp.Core;
using AndcultureCode.CSharp.Core.Extensions;
using AndcultureCode.CSharp.Core.Interfaces;
using AndcultureCode.CSharp.Core.Interfaces.Data;
using AndcultureCode.CSharp.Core.Interfaces.Entity;
using AndcultureCode.CSharp.Core.Models;
using AndcultureCode.CSharp.Core.Models.Entities;
using AndcultureCode.CSharp.Core.Models.Errors;
using AndcultureCode.CSharp.Data.SqlServer.Extensions;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace AndcultureCode.CSharp.Data.SqlServer.Repositories
{
    /// <summary>
    /// SqlServer implementation for CRUD operations for <typeparamref name="T"/>
    /// </summary>
    public partial class Repository<T> : IRepository<T> where T : Entity
    {
        #region Constants

        /// <summary>
        /// Entity could not be deleted as it does not exist
        /// </summary>
        public const string ERROR_DELETE_MISSING_ENTITY = "Repository.Delete.MissingEntity";

        /// <summary>
        /// Entity cannot be soft deleted
        /// </summary>
        public const string ERROR_DELETE_SOFT_DELETION_NOT_IDELETEABLE = "Repository.Delete.SoftDeletionNotIDeleteable";

        #endregion Constants

        #region Private Members

        private readonly IStringLocalizer _localizer;

        #endregion Private Members

        #region Properties

        public IContext Context { get; private set; }
        private DbContext DbContext { get => (DbContext)Context; }
        public IQueryable<T> Query { get; private set; }

        public int? CommandTimeout
        {
            get
            {
                if (Context != null && Context is DbContext)
                {
                    return DbContext.Database.GetCommandTimeout();
                }
                return null;
            }
            set
            {
                if (Context != null && Context is DbContext)
                {
                    DbContext.Database.SetCommandTimeout(value);
                }
            }
        }

        #endregion Properties

        #region Constructors

        /// <summary>
        /// Default constructor injecting the data context
        /// </summary>
        public Repository(IContext context, IStringLocalizer localizer)
        {
            Context = context;
            Query = context.Query<T>();

            _localizer = localizer;
        }

        #endregion Constructors

        #region IRepository Implementation

        public virtual IResult<List<T>> BulkCreate(IEnumerable<T> entities, long? createdById = default(long?))
        {
            var result = new Result<List<T>> { ResultObject = new List<T>() };
            if (entities == null || !entities.Any())
            {
                return result;
            }

            try
            {
                var entityList = entities.ToList();

                foreach (var entity in entityList)
                {
                    if (entity is ICreatable creatableEntity)
                    {
                        if (createdById.HasValue)
                        {
                            creatableEntity.CreatedById = createdById;
                        }
                        creatableEntity.CreatedOn = DateTimeOffset.UtcNow;
                    }
                }

                var index = 0;

                entityList.ForEach(e => e.Id = long.MinValue + index++);

                result.ResultObject.AddRange(entityList);

                // While utilizing EFCore, we must wrap our transaction inside a context created strategy
                // References:
                // - https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency
                // - https://github.com/aspnet/EntityFrameworkCore/issues/7318
                var strategy = DbContext.Database.CreateExecutionStrategy();
                strategy.Execute(DbContext, operation: c =>
                {
                    using (var transaction = DbContext.Database.BeginTransaction())
                    {
                        DbContext.BulkInsert(entityList, new BulkConfig { BatchSize = 1000, PreserveInsertOrder = true, SetOutputIdentity = true, UseTempDB = true });
                        transaction.Commit();
                    }
                });
            }
            catch (Exception ex)
            {
                var errorKey = ex.GetType().ToString();
                var errorMessage = $"{ex.Message} -- {ex.InnerException?.Message}";
                Console.WriteLine($"{errorKey}: {errorMessage}");
                result.AddError(errorKey, errorMessage);
                result.ResultObject = null;
            }

            return result;
        }

        public virtual IResult<List<T>> BulkCreateDistinct<TKey>(IEnumerable<T> entities, Func<T, TKey> property, long? createdById = default(long?))
            => BulkCreate(entities.DistinctBy(property), createdById);

        public IResult<bool> BulkDelete(IEnumerable<T> entities, long? deletedById = null, bool soft = true)
        {
            var result = new Result<bool> { ResultObject = false };
            if (entities == null || !entities.Any())
            {
                result.ResultObject = true;
                return result;
            }

            try
            {
                foreach (var entity in entities)
                {
                    if (!(entity is IDeletable))
                    {
                        continue;
                    }

                    if (deletedById.HasValue)
                    {
                        ((IDeletable)entity).DeletedById = deletedById;
                    }
                    ((IDeletable)entity).DeletedOn = DateTimeOffset.UtcNow;
                }

                // While utilizing EFCore, we must wrap our transaction inside a context created strategy
                // References:
                // - https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency
                // - https://github.com/aspnet/EntityFrameworkCore/issues/7318
                var strategy = DbContext.Database.CreateExecutionStrategy();
                strategy.Execute(DbContext, operation: c =>
                {
                    using (var transaction = DbContext.Database.BeginTransaction())
                    {
                        if (soft)
                        {
                            DbContext.BulkUpdate(entities.ToList(), new BulkConfig { BatchSize = 1000, UseTempDB = true });
                            transaction.Commit();
                        }
                        else
                        {
                            DbContext.BulkDelete(entities.ToList(), new BulkConfig { BatchSize = 1000 });
                            transaction.Commit();
                        }

                    }
                });
            }
            catch (Exception ex)
            {
                var errorKey = ex.GetType().ToString();
                var errorMessage = $"{ex.Message} -- {ex.InnerException?.Message}";
                Console.WriteLine($"{errorKey}: {errorMessage}");
                result.AddError(errorKey, errorMessage);
                result.ResultObject = false;
                return result;
            }

            result.ResultObject = true;

            return result;
        }

        public virtual IResult<bool> BulkUpdate(IEnumerable<T> entities, long? updatedBy = default(long?))
        {
            var result = new Result<bool> { ResultObject = false };
            if (entities == null || !entities.Any())
            {
                result.ResultObject = true;
                return result;
            }

            try
            {
                foreach (var entity in entities)
                {
                    if (entity is IUpdatable)
                    {
                        ((IUpdatable)entity).UpdatedById = updatedBy;
                        ((IUpdatable)entity).UpdatedOn = DateTimeOffset.UtcNow;
                    }
                }

                // While utilizing EFCore, we must wrap our transaction inside a context created strategy
                // References:
                // - https://docs.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency
                // - https://github.com/aspnet/EntityFrameworkCore/issues/7318
                var strategy = DbContext.Database.CreateExecutionStrategy();
                strategy.Execute(DbContext, operation: c =>
                {
                    using (var transaction = DbContext.Database.BeginTransaction())
                    {
                        DbContext.BulkUpdate(entities.ToList(), new BulkConfig { UseTempDB = true });
                        transaction.Commit();
                    }
                });

                result.ResultObject = true;
            }
            catch (Exception ex)
            {
                var errorKey = ex.GetType().ToString();
                var errorMessage = $"{ex.Message} -- {ex.InnerException?.Message}";
                Console.WriteLine($"{errorKey}: {errorMessage}");
                result.AddError(errorKey, errorMessage);
                result.ResultObject = false;
            }

            return result;
        }

        public virtual IResult<T> Create(T entity, long? createdById = default(long?))
        {
            var result = new Result<T>();

            try
            {
                if (entity is ICreatable)
                {
                    if (createdById.HasValue)
                    {
                        ((ICreatable)entity).CreatedById = createdById;
                    }
                    ((ICreatable)entity).CreatedOn = DateTimeOffset.UtcNow;
                }

                Context.Add(entity);
                Context.DetectChanges(); // Note: New to EF Core, #SaveChanges, #Add and other methods do NOT automatically call DetectChanges
                Context.SaveChanges();

                result.ResultObject = entity;
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), $"{ex.Message} -- {ex.InnerException?.Message}");
            }

            return result;
        }

        public virtual IResult<List<T>> Create(IEnumerable<T> entities, long? createdById = default(long?))
        {
            var result = new Result<List<T>> { ResultObject = new List<T>() };

            try
            {
                var numInserted = 0;

                foreach (var entity in entities)
                {
                    if (entity is ICreatable)
                    {
                        if (createdById.HasValue)
                        {
                            ((ICreatable)entity).CreatedById = createdById;
                        }
                        ((ICreatable)entity).CreatedOn = DateTimeOffset.UtcNow;
                    }

                    Context.Add(entity);
                    result.ResultObject.Add(entity);

                    // Save in batches of 100, if there are at least 100 entities.
                    if (++numInserted >= 100)
                    {
                        numInserted = 0;

                        Context.DetectChanges(); // Note: New to EF Core, #SaveChanges, #Add and other methods do NOT automatically call DetectChanges
                        Context.SaveChanges();
                    }
                }

                // Save whatever is left over.
                Context.DetectChanges(); // Note: New to EF Core, #SaveChanges, #Add and other methods do NOT automatically call DetectChanges
                Context.SaveChanges();
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), $"{ex.Message} -- {ex.InnerException?.Message}");
            }

            return result;
        }

        public IResult<List<T>> CreateDistinct<TKey>(IEnumerable<T> items, Func<T, TKey> property, long? createdById = null)
            => Create(items.DistinctBy(property), createdById);

        public virtual IResult<bool> Delete(long id, long? deletedById = default(long?), bool soft = true)
        {
            var findResult = FindById(id, true);

            if (findResult.HasErrors)
            {
                return new Result<bool>
                {
                    Errors = findResult.Errors,
                    ResultObject = false
                };
            }

            return Delete(findResult.ResultObject, deletedById, soft);
        }

        public virtual IResult<bool> Delete(T entity, long? deletedById = default(long?), bool soft = true)
        {
            var result = new Result<bool> { ResultObject = false };

            try
            {
                if (entity == null)
                {
                    result.AddError(_localizer, ERROR_DELETE_MISSING_ENTITY, $"{entity.GetType()}");
                    return result;
                }

                if (soft && !(entity is IDeletable))
                {
                    result.AddError(_localizer, ERROR_DELETE_SOFT_DELETION_NOT_IDELETEABLE);
                    return result;
                }

                if (soft)
                {
                    if (deletedById.HasValue)
                    {
                        ((IDeletable)entity).DeletedById = deletedById;
                    }
                    ((IDeletable)entity).DeletedOn = DateTimeOffset.UtcNow;
                }
                else
                {
                    Context.Delete(entity);
                }

                Context.SaveChanges();
                result.ResultObject = true;
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), ex.Message);
            }

            return result;
        }

        public virtual IResult<bool> Delete(IEnumerable<T> entities, long? deletedById = default(long?), long batchSize = 100, bool soft = true)
        {
            var result = new Result<bool>();

            try
            {
                var numDeleted = 0;

                foreach (var entity in entities)
                {
                    if (entity == null)
                    {
                        result.AddError(_localizer, ERROR_DELETE_MISSING_ENTITY, $"{entity.GetType()}");
                        continue;
                    }

                    if (soft && !(entity is IDeletable))
                    {
                        result.AddError(
                            _localizer,
                            ERROR_DELETE_SOFT_DELETION_NOT_IDELETEABLE
                        );
                        return result;
                    }

                    if (soft)
                    {
                        if (deletedById.HasValue)
                        {
                            ((IDeletable)entity).DeletedById = deletedById;
                        }
                        ((IDeletable)entity).DeletedOn = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        Context.Delete(entity);
                    }

                    if (++numDeleted >= batchSize)
                    {
                        numDeleted = 0;

                        Context.DetectChanges(); // Note: New to EF Core, #SaveChanges, #Add and other methods do NOT automatically call DetectChanges
                        Context.SaveChanges();
                    }
                }

                // Save whatever is left over.
                Context.DetectChanges(); // Note: New to EF Core, #SaveChanges, #Add and other methods do NOT automatically call DetectChanges
                Context.SaveChanges();
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), $"{ex.Message} -- {ex.InnerException?.Message}");
            }

            result.ResultObject = !result.HasErrors;

            return result;
        }

        public virtual IResult<bool> Restore(long id)
        {
            var result = new Result<bool> { ResultObject = false };

            try
            {
                var findResult = FindById(id);
                if (findResult.HasErrors)
                {
                    result.AddErrors(findResult.Errors);
                    return result;
                }

                var restoreResult = Restore(findResult.ResultObject);
                if (restoreResult.HasErrors)
                {
                    result.AddErrors(restoreResult.Errors);
                    return result;
                }

                result.ResultObject = true;
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), ex.Message);
            }

            return result;
        }

        public virtual IResult<bool> Restore(T entity)
        {
            var result = new Result<bool> { ResultObject = false };

            try
            {
                if (entity is IDeletable)
                {
                    ((IDeletable)entity).DeletedById = null;
                    ((IDeletable)entity).DeletedOn = null;
                }

                Context.Update(entity);
                Context.SaveChanges();

                result.ResultObject = true;
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), ex.Message);
            }

            return result;
        }

        public virtual IResult<bool> Update(T entity, long? updatedBy = default(long?))
        {
            var result = new Result<bool> { ResultObject = false };

            try
            {
                if (entity is IUpdatable)
                {
                    ((IUpdatable)entity).UpdatedById = updatedBy;
                    ((IUpdatable)entity).UpdatedOn = DateTimeOffset.UtcNow;
                }

                Context.Update(entity);
                Context.SaveChanges();

                result.ResultObject = true;
            }
            catch (Exception ex)
            {
                result.AddError(ex.GetType().ToString(), ex.Message);
            }

            return result;
        }

        public virtual IResult<bool> Update(IEnumerable<T> entities, long? updatedBy = default(long?)) => Do<bool>.Try((r) =>
        {
            var numUpdated = 0;

            foreach (var entity in entities)
            {
                if (entity is IUpdatable)
                {
                    ((IUpdatable)entity).UpdatedById = updatedBy;
                    ((IUpdatable)entity).UpdatedOn = DateTimeOffset.UtcNow;
                }

                Context.Update(entity);

                // Save in batches of 100, if there are at least 100 entities.
                if (++numUpdated >= 100)
                {
                    numUpdated = 0;

                    Context.SaveChanges();
                }
            }

            // Save whatever is left over.
            Context.SaveChanges();

            return true;
        })
        .Result;

        #endregion IRepository Implementation

        #region Protected Methods

        public virtual IQueryable<T> GetQueryable(
            Expression<Func<T, bool>> filter = null,
            Func<IQueryable<T>, IOrderedQueryable<T>> orderBy = null,
            string includeProperties = null,
            int? skip = null,
            int? take = null,
            bool? ignoreQueryFilters = false,
            bool asNoTracking = false
        )
        {
            includeProperties = includeProperties ?? string.Empty;
            var query = Query;

            if (ignoreQueryFilters.HasValue && ignoreQueryFilters.Value)
            {
                query = query.IgnoreQueryFilters();
            }

            if (filter != null)
            {
                query = query.Where(filter);
            }

            if (orderBy != null)
            {
                query = orderBy(query);
            }

            foreach (var includeProperty in includeProperties.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                query = query.Include(includeProperty);
            }

            if (skip.HasValue)
            {
                query = query.Skip(skip.Value);
            }

            if (take.HasValue)
            {
                query = query.Take(take.Value);
            }

            if (asNoTracking)
            {
                query = query.AsNoTracking();
            }

            return query;
        }

        public Task<IResult<List<T>>> BulkCreateAsync(IEnumerable<T> items, long? createdById = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<List<T>>> BulkCreateDistinctAsync<TKey>(IEnumerable<T> items, Func<T, TKey> property, long? createdById = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<T>> CreateAsync(T item, long? createdById = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<List<T>>> CreateAsync(IEnumerable<T> items, long? createdById = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<List<T>>> CreateDistinctAsync<TKey>(IEnumerable<T> items, Func<T, TKey> property, long? createdById = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> BulkDeleteAsync(IEnumerable<T> items, long? deletedById = null, bool soft = true)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> DeleteAsync(long id, long? deletedById = null, bool soft = true)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> DeleteAsync(T o, long? deletedById = null, bool soft = true)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> DeleteAsync(IEnumerable<T> items, long? deletedById = null, long batchSize = 100, bool soft = true)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> RestoreAsync(T o)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> RestoreAsync(long id)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> BulkUpdateAsync(IEnumerable<T> entities, long? updatedBy = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> UpdateAsync(T item, long? updatedBy = null)
        {
            throw new NotImplementedException();
        }

        public Task<IResult<bool>> UpdateAsync(IEnumerable<T> entities, long? updatedBy = null)
        {
            throw new NotImplementedException();
        }

        #endregion Protected Methods
    }
}
